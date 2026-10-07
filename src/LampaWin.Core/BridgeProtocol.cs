using System.Reflection;
using System.Text.Json;

namespace LampaWin.Core;

public static class BridgeProtocol
{
    public const int Version = 1;
    public static string Script
    {
        get
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LampaWin.Core.Web.lampawin-bridge.js")
                ?? throw new InvalidOperationException("Missing Lampa integration script.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public static string DocumentScript(Uri origin, IReadOnlyDictionary<string, string> profile)
    {
        var config = JsonSerializer.Serialize(new { origin = origin.GetLeftPart(UriPartial.Authority), profile, run = Guid.NewGuid().ToString("N") });
        return "(()=>{const c=" + config + ";if(location.origin!==c.origin||window!==window.top)return;"
            + "try{if(location.pathname!=='/bootstrap'&&sessionStorage.getItem('__lwRun')!==c.run){localStorage.clear();for(const [k,v] of Object.entries(c.profile))localStorage.setItem(k,v);sessionStorage.setItem('__lwRun',c.run);}}catch{}"
            + "try{if(!localStorage.getItem('language')){localStorage.setItem('language','ru');localStorage.setItem('tmdb_lang','ru');}}catch{}"
            + "try{if(!localStorage.getItem('navigation_type'))localStorage.setItem('navigation_type','mouse');}catch{}"
            + "window.__lampawinOrigin=c.origin;window.__lwErrors=[];window.addEventListener('error',e=>{if(window.__lwErrors.length<100)window.__lwErrors.push({type:e.error?e.error.name:'resource',file:(e.filename||e.target?.src||e.target?.href||'').split('?')[0],line:e.lineno||0})},true);})();";
    }

    public static bool IsTrustedSource(string? source, Uri origin) =>
        Uri.TryCreate(source, UriKind.Absolute, out var value) &&
        value.GetLeftPart(UriPartial.Authority).Equals(origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
}
