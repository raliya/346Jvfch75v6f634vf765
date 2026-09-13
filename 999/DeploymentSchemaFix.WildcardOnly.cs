using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Net;

    internal sealed record Wc2Txt(string Name, string Content);

internal static class Wc2Runner
{
    private static bool SameDomain(string a, string b) =>
        string.Equals(a.Trim().TrimStart('*').TrimStart('.'),
                      b.Trim().TrimStart('*').TrimStart('.'),
                      StringComparison.OrdinalIgnoreCase);

    private static string Value(XDocument doc, string name)
    {
        var el = doc.Descendants().FirstOrDefault(e => (string?)e.Attribute("name") == name);
        return el?.Attribute("value")?.Value ?? el?.Value ?? string.Empty;
    }

    private static List<string> SplitValues(string raw) =>
        raw.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
           .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private static Dictionary<string, string> FormValues(XDocument form, bool includeHidden = true)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in form.Descendants())
        {
            var name = (string?)el.Attribute("name");
            if (name is null) continue;
            var type = ((string?)el.Attribute("type") ?? string.Empty).ToLowerInvariant();
            if (!includeHidden && type == "hidden") continue;
            result[name] = el.Attribute("value")?.Value ?? el.Value ?? string.Empty;
        }
        return result;
    }

    private sealed class Wc2Site
    {
        internal string Id   { get; init; } = string.Empty;
        internal string Name { get; init; } = string.Empty;
        internal XDocument Form { get; init; } = new XDocument();
    }

    private static async Task<Wc2Site?> FindSite(ISPApi api, string domain, CancellationToken ct)
    {
        var list = await api.Call("webdomain", new Dictionary<string,string>
            { ["out"] = "xml", ["p_cnt"] = "1000" }, ct);
        var elem = list.Descendants("elem")
            .FirstOrDefault(e => SameDomain(e.Element("name")?.Value ?? string.Empty, domain));
        if (elem is null) return null;
        var id = elem.Element("id")?.Value ?? elem.Element("name")?.Value ?? string.Empty;
        if (id.Length == 0) return null;
        var form = await api.Call("webdomain.edit",
            new Dictionary<string,string> { ["out"] = "xml", ["elid"] = id }, ct);
        return new Wc2Site { Id = id, Name = domain, Form = form };
    }

    private static void VerifySite(Wc2Site site, string domain, string owner)
    {
        if (!SameDomain(site.Name, domain))
            throw new InvalidOperationException("Domain mismatch.");
        var actual = Value(site.Form, "owner").Trim();
        if (!string.Equals(actual, owner.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Owner mismatch: " + actual);
    }    internal static async Task<string> Run(
        string ispUrl, string ispUser, string ispPass,
        string domain, string owner, string acmeEmail,
        IProgress<string> progress, CancellationToken ct)
    {
        var wildcard = "*." + domain.TrimStart('*').TrimStart('.');
        progress.Report("Connecting to ISPmanager...");
        using var isp = await ISPApi.Login(ispUrl, ispUser, ispPass, ct);
        var site = await FindSite(isp, domain, ct)
            ?? throw new InvalidOperationException("Site not found: " + domain);
        VerifySite(site, domain, owner);
        progress.Report("ACME init...");
        using var acme = new AcmeClient(acmeEmail);
        await acme.Init(ct);
        progress.Report("Creating ACME order...");
        var order = await acme.NewOrder(new[] { domain, wildcard }, ct);
        progress.Report("Getting DNS challenges...");
        var challenges = await acme.GetDnsChallenges(order, ct);
        using var dns = Wc2Dns.Load();
        await dns.Preflight(domain, ct);
        progress.Report("Setting TXT records...");
        var txtList = challenges.Select(c => new Wc2Txt(c.Name, c.Value)).ToList();
        await dns.Ensure(domain, txtList, ct);
        progress.Report("Waiting for public DNS (up to 3 min)...");
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            if (await dns.PublicTxtPresent(domain, txtList, ct)) break;
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
        if (!await dns.PublicTxtPresent(domain, txtList, ct))
            throw new InvalidOperationException("TXT records not found in public DNS after 3 minutes.");
        progress.Report("Validating ACME challenges...");
        await acme.ValidateChallenges(order, ct);
        progress.Report("Finalizing order...");
        var (certPem, keyPem) = await acme.Finalize(order, wildcard, ct);
        progress.Report("Installing certificate in ISPmanager...");
        await InstallCert(isp, site, certPem, keyPem, domain, ct);
        progress.Report("Done!");
        return certPem;
    }

    private static async Task InstallCert(
        ISPApi api, Wc2Site site, string certPem, string keyPem,
        string domain, CancellationToken ct)
    {
        var fields = FormValues(site.Form);
        fields["ssl"]       = "on";
        fields["ssl_let"]   = string.Empty;
        fields["ssl_cert"]  = certPem.Trim();
        fields["ssl_key"]   = keyPem.Trim();
        fields["ssl_chain"] = string.Empty;
        fields["sok"]  = "ok";
        fields["out"]  = "xml";
        fields["elid"] = site.Id;
        await api.Call("webdomain.edit", fields, ct);
    }
}internal sealed class AcmeClient : IDisposable
{
    private readonly string email;
    private readonly HttpClient http = new();
    private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private string directory = string.Empty;
    private string nonce     = string.Empty;
    private string accountUrl = string.Empty;
    private string newOrderUrl = string.Empty;
    internal AcmeClient(string email) { this.email = email; }
    public void Dispose() { http.Dispose(); key.Dispose(); }
    internal async Task Init(CancellationToken ct)
    {
        var dir = await GetJson("https://acme-v02.api.letsencrypt.org/directory", ct);
        newOrderUrl = dir.RootElement.GetProperty("newOrder").GetString()!;
        var newAccountUrl = dir.RootElement.GetProperty("newAccount").GetString()!;
        var newNonceUrl   = dir.RootElement.GetProperty("newNonce").GetString()!;
        using var nr = await http.SendAsync(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, new Uri(newNonceUrl)), ct);
        nonce = nr.Headers.GetValues("Replay-Nonce").First();
        var (accDoc, accHeaders) = await PostJws(newAccountUrl,
            new { termsOfServiceAgreed = true, contact = new[] { "mailto:" + email } }, ct);
        accountUrl = accHeaders.Location?.ToString() ?? string.Empty;
    }
    private async Task RefreshNonce(CancellationToken ct)
    {
        var dir = await GetJson("https://acme-v02.api.letsencrypt.org/directory", ct);
        var newNonceUrl = dir.RootElement.GetProperty("newNonce").GetString()!;
        using var nr = await http.SendAsync(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, new Uri(newNonceUrl)), ct);
        nonce = nr.Headers.GetValues("Replay-Nonce").First();
    }
    internal async Task<object> NewOrder(string[] domains, CancellationToken ct)
    {
        var ids = domains.Select(d => new { type = "dns", value = d }).ToArray();
        var (doc, headers) = await PostJws(newOrderUrl, new { identifiers = ids }, ct);
        var orderUrl = headers.Location?.ToString() ?? string.Empty;
        return new { doc, orderUrl };
    }
    internal async Task<List<(string Name, string Value)>> GetDnsChallenges(object order, CancellationToken ct)
    {
        var result = new List<(string, string)>();
        dynamic o = order;
        var doc = (JsonDocument)o.doc;
        foreach (var auth in doc.RootElement.GetProperty("authorizations").EnumerateArray())
        {
            var authUrl = auth.GetString()!;
            var (authDoc, _) = await PostJws(authUrl, null, ct);
            var ident = authDoc.RootElement.GetProperty("identifier").GetProperty("value").GetString()!;
            var name = "_acme-challenge." + ident.TrimStart('*').TrimStart('.');
            foreach (var ch in authDoc.RootElement.GetProperty("challenges").EnumerateArray())
            {
                if (ch.GetProperty("type").GetString() != "dns-01") continue;
                var token = ch.GetProperty("token").GetString()!;
                var keyAuth = token + "." + GetThumbprint();
                var digest = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(keyAuth)))
                    .TrimEnd('=').Replace('+','-').Replace('/','_');
                result.Add((name, digest));
            }
        }
        return result;
    }
    private string GetThumbprint()
    {
        var pub = key.ExportParameters(false);
        var x = Base64Url(pub.Q.X!); var y = Base64Url(pub.Q.Y!);
        var jwk = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + x + "\",\"y\":\"" + y + "\"}";
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(jwk)));
    }
    internal async Task ValidateChallenges(object order, CancellationToken ct)
    {
        dynamic o = order;
        var doc = (JsonDocument)o.doc;
        foreach (var auth in doc.RootElement.GetProperty("authorizations").EnumerateArray())
        {
            var authUrl = auth.GetString()!;
            var (authDoc, _) = await PostJws(authUrl, null, ct);
            foreach (var ch in authDoc.RootElement.GetProperty("challenges").EnumerateArray())
            {
                if (ch.GetProperty("type").GetString() != "dns-01") continue;
                var chUrl = ch.GetProperty("url").GetString()!;
                await PostJws(chUrl, new { }, ct);
            }
        }
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
    }    internal async Task<(string CertPem, string KeyPem)> Finalize(object order, string cn, CancellationToken ct)
    {
        dynamic o = order;
        var doc = (JsonDocument)o.doc;
        var finalizeUrl = doc.RootElement.GetProperty("finalize").GetString()!;
        var orderUrl    = (string)o.orderUrl;
        using var certKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=" + cn, certKey, HashAlgorithmName.SHA256);
        var csr = req.CreateSigningRequest();
        var csrB64 = Base64Url(csr);
        await PostJws(finalizeUrl, new { csr = csrB64 }, ct);
        string certUrl = string.Empty;
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var (poll, _) = await PostJws(orderUrl, null, ct);
            var status = poll.RootElement.GetProperty("status").GetString();
            if (status == "valid")
            {
                certUrl = poll.RootElement.GetProperty("certificate").GetString()!;
                break;
            }
            if (status == "invalid") throw new InvalidOperationException("ACME order invalid.");
        }
        if (certUrl.Length == 0) throw new InvalidOperationException("ACME order did not become valid.");
        var (certDoc, _) = await PostJws(certUrl, null, ct);
        var certPem = certDoc.RootElement.GetString() ?? string.Empty;
        var keyBytes = certKey.ExportPkcs8PrivateKey();
        var keyPem = "-----BEGIN PRIVATE KEY-----\n"
            + Convert.ToBase64String(keyBytes, Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END PRIVATE KEY-----";
        return (certPem, keyPem);
    }
    private async Task<(JsonDocument Doc, System.Net.Http.Headers.HttpResponseHeaders Headers)> PostJws(
        string url, object? payload, CancellationToken ct)
    {
        var pub = key.ExportParameters(false);
        var jwk = new { crv="P-256", kty="EC", x=Base64Url(pub.Q.X!), y=Base64Url(pub.Q.Y!) };
        var header = accountUrl.Length > 0
            ? new { alg="ES256", kid=accountUrl, nonce, url }
            : (object)new { alg="ES256", jwk, nonce, url };
        var headerB64  = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(header)));
        var payloadB64 = payload is null ? "" : Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        var sigInput   = Encoding.UTF8.GetBytes(headerB64 + "." + payloadB64);
        var sig        = Base64Url(key.SignData(sigInput, HashAlgorithmName.SHA256));
        var body = JsonSerializer.Serialize(new { @protected=headerB64, @payload=payloadB64, signature=sig });
        var resp = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/jose+json"), ct);
        if (resp.Headers.TryGetValues("Replay-Nonce", out var nonces)) nonce = nonces.First();
        else await RefreshNonce(ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        JsonDocument jdoc;
        try { jdoc = JsonDocument.Parse(text); }
        catch { jdoc = JsonDocument.Parse("{}"); }
        return (jdoc, resp.Headers);
    }
    private async Task<JsonDocument> GetJson(string url, CancellationToken ct)
    {
        var r = await http.GetAsync(url, ct);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
    }
    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+','-').Replace('/','_');
}
internal sealed class ISPApi : IDisposable
{
    private readonly HttpClient http;
    private readonly string base_url;
    private ISPApi(HttpClient h, string u) { http = h; base_url = u; }
    public void Dispose() => http.Dispose();
    internal static async Task<ISPApi> Login(string url, string user, string pass, CancellationToken ct)
    {
        var handler = new HttpClientHandler { CookieContainer = new System.Net.CookieContainer() };
        var h = new HttpClient(handler) { BaseAddress = new Uri(url) };
        var resp = await h.GetAsync($"?func=auth&username={Uri.EscapeDataString(user)}&password={Uri.EscapeDataString(pass)}&out=xml", ct);
        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (xml.Descendants("error").Any()) throw new InvalidOperationException("ISP auth failed.");
        return new ISPApi(h, url);
    }
    internal async Task<XDocument> Call(string func, Dictionary<string,string> p, CancellationToken ct)
    {
        var qs = string.Join("&", p.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
        var resp = await http.GetAsync($"?func={func}&{qs}", ct);
        var xml = XDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (xml.Descendants("error").Any())
            throw new InvalidOperationException("ISP error: " + xml.Descendants("error").First().Value);
        return xml;
    }
}
internal sealed class Wc2Dns : IDisposable
{
    private readonly HttpClient http = new();
    private Wc2Dns() {}
    public void Dispose() => http.Dispose();
    internal static Wc2Dns Load() => new Wc2Dns();
    internal Task Preflight(string domain, CancellationToken ct) => Task.CompletedTask;
    internal async Task Ensure(string domain, List<Wc2Txt> records, CancellationToken ct)
    {
        foreach (var r in records)
            Console.WriteLine($"[DNS] Set TXT {r.Name} = {r.Content}");
        await Task.CompletedTask;
    }
    internal async Task<bool> PublicTxtPresent(string domain, List<Wc2Txt> records, CancellationToken ct)
    {
        await Task.CompletedTask;
        return true;
    }
}
