#!/usr/bin/env python3
"""Local-only BitTorrent v1 tracker and seed for TorrServer/VLC smoke tests."""

from __future__ import annotations

import argparse
import hashlib
import http.server
import json
import os
import signal
import socket
import socketserver
import struct
import sys
import threading
import time
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any

PIECE_LENGTH = 16 * 1024
BLOCK_LENGTH = 16 * 1024
PROTOCOL = b"BitTorrent protocol"
PEER_ID = b"-LW0100-LOCALFIXTURE"
assert len(PEER_ID) == 20


def bencode(value: Any) -> bytes:
    if isinstance(value, int):
        return b"i" + str(value).encode("ascii") + b"e"
    if isinstance(value, bytes):
        return str(len(value)).encode("ascii") + b":" + value
    if isinstance(value, list):
        return b"l" + b"".join(bencode(item) for item in value) + b"e"
    if isinstance(value, dict):
        return b"d" + b"".join(bencode(key) + bencode(value[key]) for key in sorted(value)) + b"e"
    raise TypeError(f"Unsupported bencode value: {type(value).__name__}")


def bdecode(data: bytes) -> Any:
    """Small strict decoder used only by the self-test."""
    def parse(index: int) -> tuple[Any, int]:
        if index >= len(data):
            raise ValueError("truncated bencode")
        token = data[index:index + 1]
        if token == b"i":
            end = data.index(b"e", index)
            return int(data[index + 1:end]), end + 1
        if token == b"l":
            items, index = [], index + 1
            while data[index:index + 1] != b"e":
                item, index = parse(index)
                items.append(item)
            return items, index + 1
        if token == b"d":
            result, index = {}, index + 1
            while data[index:index + 1] != b"e":
                key, index = parse(index)
                value, index = parse(index)
                result[key] = value
            return result, index + 1
        colon = data.index(b":", index)
        size = int(data[index:colon])
        if size < 0:
            raise ValueError("negative byte string size")
        end = colon + 1 + size
        if end > len(data):
            raise ValueError("truncated byte string")
        return data[colon + 1:end], end

    value, end = parse(0)
    if end != len(data):
        raise ValueError("trailing bencode data")
    return value


def recv_exact(connection: socket.socket, size: int) -> bytes:
    chunks = bytearray()
    while len(chunks) < size:
        part = connection.recv(size - len(chunks))
        if not part:
            raise ConnectionError("peer disconnected")
        chunks.extend(part)
    return bytes(chunks)


class Fixture:
    def __init__(self, video: Path, peer_host: str = "127.0.0.1"):
        self.video = video.resolve(strict=True)
        self.content = self.video.read_bytes()
        self.peer_host = peer_host
        self.peer_ip = socket.inet_aton(peer_host)
        if not self.content:
            raise ValueError("fixture video is empty")
        if len(self.content) > 16 * 1024 * 1024:
            raise ValueError("fixture video must be 16 MiB or smaller")
        self.pieces = [self.content[i:i + PIECE_LENGTH] for i in range(0, len(self.content), PIECE_LENGTH)]
        self.piece_hashes = b"".join(hashlib.sha1(piece).digest() for piece in self.pieces)
        self.info = {
            b"length": len(self.content),
            b"name": b"fixture.mp4",
            b"piece length": PIECE_LENGTH,
            b"pieces": self.piece_hashes,
        }
        self.info_hash = hashlib.sha1(bencode(self.info)).digest()
        self.stopped = threading.Event()
        self.stats_lock = threading.Lock()
        self.stats = {
            "announces": 0,
            "validAnnounces": 0,
            "peerConnections": 0,
            "handshakeAttempts": 0,
            "unsupportedHandshakeHeaders": 0,
            "peerHandshakes": 0,
            "pieceRequests": 0,
            "registeredPeers": 0,
        }
        self.registered_peers: dict[bytes, tuple[str, int]] = {}
        self.http = http.server.ThreadingHTTPServer(("127.0.0.1", 0), self._http_handler())
        self.http.daemon_threads = True
        self.peer = socketserver.ThreadingTCPServer((self.peer_host, 0), self._peer_handler())
        self.peer.allow_reuse_address = True
        self.peer.daemon_threads = True
        self.peer_port = self.peer.server_address[1]
        self.torrent = bencode({
            b"announce": self.tracker_url.encode("ascii"),
            b"created by": b"LampaWin local smoke test",
            b"info": self.info,
        })
        self.http_thread = threading.Thread(target=self.http.serve_forever, name="fixture-tracker", daemon=True)
        self.peer_thread = threading.Thread(target=self.peer.serve_forever, name="fixture-seed", daemon=True)

    @property
    def tracker_url(self) -> str:
        return f"http://127.0.0.1:{self.http.server_address[1]}/announce"

    @property
    def torrent_url(self) -> str:
        return f"http://127.0.0.1:{self.http.server_address[1]}/fixture.torrent"

    @property
    def manifest(self) -> dict[str, Any]:
        return {
            "torrentUrl": self.torrent_url,
            "infoHash": self.info_hash.hex(),
            "peerPort": self.peer_port,
            "peerHost": self.peer_host,
            "sizeBytes": len(self.content),
            "pieceLength": PIECE_LENGTH,
        }

    def _http_handler(self):
        fixture = self

        class Handler(http.server.BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, _format: str, *_args: object) -> None:
                pass

            def respond(self, status: int, body: bytes, content_type: str) -> None:
                self.send_response(status)
                self.send_header("Content-Type", content_type)
                self.send_header("Content-Length", str(len(body)))
                self.send_header("Connection", "close")
                self.end_headers()
                self.wfile.write(body)

            def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
                path = urllib.parse.urlsplit(self.path).path
                if path == "/fixture.torrent":
                    self.respond(200, fixture.torrent, "application/x-bittorrent")
                    return
                if path == "/announce":
                    with fixture.stats_lock:
                        fixture.stats["announces"] += 1
                    values = {}
                    for pair in urllib.parse.urlsplit(self.path).query.split("&"):
                        if "=" in pair:
                            key, value = pair.split("=", 1)
                            values[urllib.parse.unquote_plus(key)] = urllib.parse.unquote_to_bytes(value.replace("+", " "))
                    if values.get("info_hash") != fixture.info_hash:
                        self.respond(200, bencode({b"failure reason": b"unknown info_hash"}), "text/plain")
                        return
                    if len(values.get("peer_id", b"")) != 20:
                        self.respond(200, bencode({b"failure reason": b"invalid peer_id"}), "text/plain")
                        return
                    with fixture.stats_lock:
                        fixture.stats["validAnnounces"] += 1
                        peer_id = values["peer_id"]
                        announced_port = int(values.get("port", b"0"))
                        left = int(values.get("left", b"1"))
                        event = values.get("event", b"")
                        if announced_port > 0 and left == 0 and event != b"stopped":
                            fixture.registered_peers[peer_id] = (fixture.peer_host, announced_port)
                        elif event == b"stopped":
                            fixture.registered_peers.pop(peer_id, None)
                        fixture.stats["registeredPeers"] = len(fixture.registered_peers)
                    interval = 30
                    compact_peers = b"".join(socket.inet_aton(host) + struct.pack("!H", port)
                        for peer, (host, port) in fixture.registered_peers.items() if peer != values["peer_id"])
                    if not compact_peers:
                        compact_peers = fixture.peer_ip + struct.pack("!H", fixture.peer_port)
                    response = bencode({
                        b"complete": 1,
                        b"incomplete": 0,
                        b"interval": interval,
                        b"peers": compact_peers,
                    })
                    self.respond(200, response, "text/plain")
                    return
                if path == "/health":
                    self.respond(200, b"ok\n", "text/plain")
                    return
                if path == "/stats":
                    with fixture.stats_lock:
                        body = json.dumps(fixture.stats, separators=(",", ":")).encode("ascii")
                    self.respond(200, body, "application/json")
                    return
                self.respond(404, b"not found\n", "text/plain")

        return Handler

    def _peer_handler(self):
        fixture = self

        class Handler(socketserver.BaseRequestHandler):
            def handle(self) -> None:
                connection: socket.socket = self.request
                connection.settimeout(15)
                try:
                    with fixture.stats_lock:
                        fixture.stats["peerConnections"] += 1
                    protocol_length = recv_exact(connection, 1)
                    if protocol_length != bytes([len(PROTOCOL)]):
                        with fixture.stats_lock:
                            fixture.stats["unsupportedHandshakeHeaders"] += 1
                        return
                    with fixture.stats_lock:
                        fixture.stats["handshakeAttempts"] += 1
                    protocol = recv_exact(connection, len(PROTOCOL))
                    if protocol != PROTOCOL:
                        with fixture.stats_lock:
                            fixture.stats["unsupportedHandshakeHeaders"] += 1
                        return
                    reserved = recv_exact(connection, 8)
                    info_hash = recv_exact(connection, 20)
                    recv_exact(connection, 20)  # remote peer id
                    if info_hash != fixture.info_hash or fixture.stopped.is_set():
                        return
                    with fixture.stats_lock:
                        fixture.stats["peerHandshakes"] += 1
                    # Do not echo remote extension bits: only advertise features this fixture implements.
                    connection.sendall(bytes([len(PROTOCOL)]) + PROTOCOL + bytes(8) + fixture.info_hash + PEER_ID)
                    bitfield = bytearray((len(fixture.pieces) + 7) // 8)
                    for index in range(len(fixture.pieces)):
                        bitfield[index // 8] |= 1 << (7 - index % 8)
                    connection.sendall(struct.pack("!I", len(bitfield) + 1) + b"\x05" + bitfield)
                    connection.sendall(struct.pack("!IB", 1, 1))  # unchoke
                    while not fixture.stopped.is_set():
                        header = recv_exact(connection, 4)
                        message_length = struct.unpack("!I", header)[0]
                        if message_length == 0:  # keep-alive
                            continue
                        if message_length > 64 * 1024:
                            return
                        message = recv_exact(connection, message_length)
                        message_id, payload = message[0], message[1:]
                        if message_id != 6 or len(payload) != 12:  # only request messages need a response
                            continue
                        with fixture.stats_lock:
                            fixture.stats["pieceRequests"] += 1
                        piece_index, begin, block_size = struct.unpack("!III", payload)
                        if piece_index >= len(fixture.pieces) or block_size == 0 or block_size > BLOCK_LENGTH:
                            return
                        piece = fixture.pieces[piece_index]
                        if begin >= len(piece) or begin + block_size > len(piece):
                            return
                        block = piece[begin:begin + block_size]
                        response_payload = b"\x07" + struct.pack("!II", piece_index, begin) + block
                        connection.sendall(struct.pack("!I", len(response_payload)) + response_payload)
                except (ConnectionError, OSError, ValueError, struct.error):
                    return

        return Handler

    def start(self) -> None:
        self.http_thread.start()
        self.peer_thread.start()

    def close(self) -> None:
        if self.stopped.is_set():
            return
        self.stopped.set()
        self.http.shutdown()
        self.peer.shutdown()
        self.http.server_close()
        self.peer.server_close()
        self.http_thread.join(timeout=2)
        self.peer_thread.join(timeout=2)


def compact_announce_url(tracker: str, info_hash: bytes, peer_id: bytes, port: int, left: int) -> str:
    query = urllib.parse.urlencode({
        "info_hash": info_hash,
        "peer_id": peer_id,
        "port": port,
        "uploaded": 0,
        "downloaded": 0,
        "left": left,
        "compact": 1,
        "event": "started",
    }, quote_via=urllib.parse.quote)
    return tracker + "?" + query


def verify_fixture(fixture: Fixture) -> dict[str, Any]:
    with urllib.request.urlopen(fixture.torrent_url, timeout=3) as response:
        torrent = bdecode(response.read())
    info = torrent[b"info"]
    if hashlib.sha1(bencode(info)).digest() != fixture.info_hash:
        raise AssertionError("torrent infohash mismatch")
    if info[b"pieces"] != fixture.piece_hashes or info[b"piece length"] != PIECE_LENGTH:
        raise AssertionError("torrent piece hashes/size mismatch")

    client_id = b"-LW0100-SELFTEST1234"
    announce = compact_announce_url(fixture.tracker_url, fixture.info_hash, client_id, 51413, len(fixture.content))
    with urllib.request.urlopen(announce, timeout=3) as response:
        tracker = bdecode(response.read())
    expected_peer = fixture.peer_ip + struct.pack("!H", fixture.peer_port)
    if tracker[b"peers"] != expected_peer:
        raise AssertionError("tracker returned the wrong localhost peer")

    downloaded = bytearray()
    with socket.create_connection((fixture.peer_host, fixture.peer_port), timeout=3) as connection:
        connection.settimeout(3)
        connection.sendall(bytes([len(PROTOCOL)]) + PROTOCOL + bytes(8) + fixture.info_hash + client_id)
        handshake = recv_exact(connection, 68)
        if handshake[28:48] != fixture.info_hash:
            raise AssertionError("seed handshake returned wrong infohash")
        piece_count = len(fixture.pieces)
        bitfield_size = (piece_count + 7) // 8
        message_length = struct.unpack("!I", recv_exact(connection, 4))[0]
        bitfield_message = recv_exact(connection, message_length)
        if bitfield_message[0] != 5 or len(bitfield_message) != bitfield_size + 1:
            raise AssertionError("seed bitfield was malformed")
        message_length = struct.unpack("!I", recv_exact(connection, 4))[0]
        unchoke = recv_exact(connection, message_length)
        if unchoke != b"\x01":
            raise AssertionError("seed did not unchoke test peer")
        for index, piece in enumerate(fixture.pieces):
            request_payload = b"\x06" + struct.pack("!III", index, 0, len(piece))
            connection.sendall(struct.pack("!I", len(request_payload)) + request_payload)
            message_length = struct.unpack("!I", recv_exact(connection, 4))[0]
            block_message = recv_exact(connection, message_length)
            if len(block_message) < 9 or block_message[0] != 7:
                raise AssertionError(f"missing piece response {index}")
            received_index, begin = struct.unpack("!II", block_message[1:9])
            block = block_message[9:]
            if received_index != index or begin != 0 or hashlib.sha1(block).digest() != hashlib.sha1(piece).digest():
                raise AssertionError(f"piece integrity check failed at {index}")
            downloaded.extend(block)
    if bytes(downloaded) != fixture.content:
        raise AssertionError("reassembled seed content differs from fixture")
    return {
        "success": True,
        "infoHash": fixture.info_hash.hex(),
        "piecesVerified": len(fixture.pieces),
        "downloadedBytes": len(downloaded),
        "tracker": True,
        "peerHandshake": True,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--video", type=Path, default=Path(".cache/fixture.mp4"), help="generated MP4 fixture")
    parser.add_argument("--peer-host", default="127.0.0.1", help="exact IPv4 address to bind/advertise the seed peer")
    parser.add_argument("--self-test", action="store_true", help="verify tracker, handshake, and every piece")
    args = parser.parse_args()
    try:
        fixture = Fixture(args.video, args.peer_host)
        fixture.start()
        try:
            if args.self_test:
                print(json.dumps(verify_fixture(fixture), separators=(",", ":")), flush=True)
                return 0
            print(json.dumps(fixture.manifest, separators=(",", ":")), flush=True)
            while not fixture.stopped.wait(1):
                pass
            return 0
        finally:
            fixture.close()
    except KeyboardInterrupt:
        return 0
    except Exception as error:
        print(json.dumps({"success": False, "error": type(error).__name__, "message": str(error)}), file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
