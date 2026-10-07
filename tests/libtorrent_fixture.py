#!/usr/bin/env python3
"""Local-only MSE-capable libtorrent seed for TorrServer stream smoke tests."""
from __future__ import annotations

import argparse
import json
import signal
import sys
import tempfile
import time
from pathlib import Path

import libtorrent as lt

from torrent_fixture import Fixture


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--video", type=Path, default=Path(".cache/fixture.mp4"))
    parser.add_argument("--peer-host", required=True, help="exact local IPv4 interface to bind/advertise")
    args = parser.parse_args()

    fixture = Fixture(args.video, args.peer_host)
    fixture.start()
    session = None
    try:
        with tempfile.TemporaryDirectory(prefix="lampawin-lt-seed-") as temp_dir:
            torrent_path = Path(temp_dir) / "fixture.torrent"
            torrent_path.write_bytes(fixture.torrent)
            settings = lt.default_settings()
            settings.update({
                "listen_interfaces": f"{args.peer_host}:0",
                "enable_dht": False,
                "enable_lsd": False,
                "enable_upnp": False,
                "enable_natpmp": False,
                "broadcast_lsd": False,
                "announce_to_all_trackers": False,
                "announce_to_all_tiers": False,
                "allow_multiple_connections_per_ip": True,
                "alert_mask": 0,
            })
            session = lt.session(settings)
            info = lt.torrent_info(str(torrent_path))
            params = lt.add_torrent_params()
            params.ti = info
            params.save_path = str(args.video.resolve().parent)
            params.seed_mode = True
            handle = session.add_torrent(params)
            deadline = time.monotonic() + 25
            while time.monotonic() < deadline:
                status = handle.status()
                if status.is_seeding:
                    break
                time.sleep(0.1)
            else:
                raise RuntimeError("libtorrent did not enter seeding state")

            port = int(session.listen_port())
            if port <= 0:
                raise RuntimeError("libtorrent did not bind its local peer listener")
            # Windows cannot announce to a loopback tracker from a socket bound to
            # the physical adapter. Add a loopback listener using the same peer port;
            # the fixture still advertises only its physical adapter to TorrServer.
            if args.peer_host != "127.0.0.1":
                session.apply_settings({"listen_interfaces": f"{args.peer_host}:{port},127.0.0.1:{port}"})
            # Seed-mode starts with all content present. Force a tracker announce and
            # wait until the local tracker has registered this MSE-capable peer.
            handle.force_reannounce()
            announce_deadline = time.monotonic() + 15
            while time.monotonic() < announce_deadline:
                for alert in session.pop_alerts():
                    if isinstance(alert, (lt.tracker_error_alert, lt.tracker_warning_alert)):
                        print(json.dumps({"fixtureNotice": type(alert).__name__, "message": str(alert.message())}), file=sys.stderr, flush=True)
                if fixture.stats["registeredPeers"] > 0:
                    break
                time.sleep(0.1)
            if fixture.stats["registeredPeers"] == 0:
                raise RuntimeError(f"local tracker did not register libtorrent seed: {fixture.stats}; trackers={handle.trackers()}")

            manifest = fixture.manifest
            manifest.update({"peerPort": port, "seed": "libtorrent-mse"})
            print(json.dumps(manifest, separators=(",", ":")), flush=True)
            while True:
                time.sleep(1)
    except KeyboardInterrupt:
        return 0
    except Exception as error:
        print(json.dumps({"success": False, "error": type(error).__name__, "detail": str(error)}), file=sys.stderr, flush=True)
        return 1
    finally:
        if session is not None:
            session.pause()
            session = None
        fixture.close()


if __name__ == "__main__":
    raise SystemExit(main())
