using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> data)
    {
        var s = Convert.ToBase64String(data).TrimEnd('=');
        return s.Replace('+', '-').Replace('/', '_');
    }
    public static byte[] Decode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}

sealed class AgentPayload
{
    public int v { get; set; } = 1;
    public Dictionary<string, object> agent { get; set; } = new();
    public string[]? scopes { get; set; }
    public DateTimeOffset? exp { get; set; }
    public string nonce { get; set; } = Guid.NewGuid().ToString("N");
}

static class QueryUtil
{
    public static Dictionary<string,string> Parse(string query)
    {
        var dict = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return dict;
        var q = query.TrimStart('?');
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            var key = Uri.UnescapeDataString(kv[0]);
            var val = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            dict[key] = val;
        }
        return dict;
    }
}

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help") { PrintHelp(); return 0; }
        var cmd  = args[0].ToLowerInvariant();
        var dict = ParseArgs(args.Skip(1).ToArray());
        try
        {
            return cmd switch
            {
                "gen"    => CmdGen(dict),
                "verify" => CmdVerify(dict),
                _        => Show("Unbekannter Befehl. Nutze --help", 1)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fehler: {ex.Message}");
            return 2;
        }
    }

    static int CmdGen(Dictionary<string,string> a)
    {
        string? secret = GetSecret(a);
        if (string.IsNullOrWhiteSpace(secret))
            return Show("Secret fehlt. Nutze --secret, --secret-file oder --secret-env.", 1);

        AgentPayload payload;
        if (a.TryGetValue("payload-file", out var pf))
        {
            var json = File.ReadAllText(pf);
            payload = JsonSerializer.Deserialize<AgentPayload>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                      ?? throw new Exception("Ungültige Payload-Datei");
        }
        else
        {
            payload = new AgentPayload();
            if (a.TryGetValue("name",  out var name))  payload.agent["name"] = name;
            if (a.TryGetValue("role",  out var role))  payload.agent["role"] = role;
            if (a.TryGetValue("scopes",out var scps))  payload.scopes = scps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (a.TryGetValue("exp",   out var exp))   payload.exp = DateTimeOffset.Parse(exp);
        }

        string jsonPayload = JsonSerializer.Serialize(payload);
        string p64 = Base64Url.Encode(Encoding.UTF8.GetBytes(jsonPayload));

        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret!));
        string sig = Base64Url.Encode(h.ComputeHash(Encoding.UTF8.GetBytes(p64)));

        string scheme = a.TryGetValue("scheme", out var sc) ? sc : "gglas";
        string path   = a.TryGetValue("path",   out var pa) ? pa : "agent/new";

        string url = $"{scheme}://{path}?payload={p64}&sig={sig}";
        Console.WriteLine(url);
        return 0;
    }

    static int CmdVerify(Dictionary<string,string> a)
    {
        if (!a.TryGetValue("url", out var url)) return Show("--url fehlt", 1);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return Show("Ungültige URL", 1);

        var qp = QueryUtil.Parse(uri.Query);
        if (!qp.TryGetValue("payload", out var p64) || !qp.TryGetValue("sig", out var sig))
            return Show("payload/sig fehlt", 1);

        string? secret = GetSecret(a);
        if (string.IsNullOrWhiteSpace(secret))
            return Show("Secret fehlt. Nutze --secret, --secret-file oder --secret-env.", 1);

        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret!));
        // Vergleiche im Byteformat (konstante Zeit), um Base64-Varianten zu vermeiden
        var sigCalcBytes = h.ComputeHash(Encoding.UTF8.GetBytes(p64));
        var sigBytes     = Base64Url.Decode(sig);
        if (sigBytes.Length != sigCalcBytes.Length || !CryptographicOperations.FixedTimeEquals(sigBytes, sigCalcBytes))
        {
            Console.WriteLine("Signatur ungültig");
            return 1;
        }

        var json = Encoding.UTF8.GetString(Base64Url.Decode(p64));
        var payload = JsonSerializer.Deserialize<AgentPayload>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (payload?.exp is not null && DateTimeOffset.UtcNow > payload.exp)
        {
            Console.WriteLine("Payload abgelaufen (exp)");
            return 1;
        }

        Console.WriteLine("OK");
        Console.WriteLine(json);
        return 0;
    }

    static string? GetSecret(Dictionary<string,string> a)
    {
        if (a.TryGetValue("secret", out var s)) return s;
        if (a.TryGetValue("secret-file", out var sf)) return File.ReadAllText(sf).Trim();
        if (a.TryGetValue("secret-env",  out var se)) return Environment.GetEnvironmentVariable(se);
        return null;
    }

    static Dictionary<string,string> ParseArgs(string[] a)
    {
        var d = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i].StartsWith("--"))
            {
                var key = a[i][2..];
                var val = (i + 1 < a.Length && !a[i + 1].StartsWith("--")) ? a[++i] : "true";
                d[key] = val;
            }
        }
        return d;
    }

    static int Show(string msg, int code) { Console.Error.WriteLine(msg); return code; }

    static void PrintHelp()
    {
        Console.WriteLine(@"gglas-linker - Generator/Verifier für gglas:// Links

Verwendung:
  gglas-linker gen [--name NAME] [--role ROLE] [--scopes s1,s2] [--exp 2025-12-31T23:59:59Z]
                   [--payload-file file.json]
                   [--secret abc] [--secret-file path] [--secret-env VAR]
                   [--scheme gglas] [--path agent/new]

  gglas-linker verify --url ""gglas://agent/new?...""
                      [--secret abc] [--secret-file path] [--secret-env VAR]

Beispiele:
  setx GGLAS_SECRET ""mysupersecret""
  gglas-linker gen --name Alice --role research --scopes web,files --exp 2025-12-31T23:59:59Z --secret-env GGLAS_SECRET
  gglas-linker verify --url ""gglas://agent/new?payload=...&sig=..."" --secret-env GGLAS_SECRET
");
    }
}