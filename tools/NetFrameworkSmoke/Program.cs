// Exercises the net472 (.NET Framework) build of Crowdhandler.NETsdk end to end against a real account. Runs on .NET Framework
// (Windows) or Mono (macOS/Linux): mono bin/Release/net472/NetFrameworkSmoke.exe <publicKey> <privateKey> <apiEndpoint> <wrEndpoint> <testHost>
using System;
using System.Linq;
using Crowdhandler.NETsdk;
using Crowdhandler.NETsdk.JSONTypes;
using Newtonsoft.Json;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length < 5) { Console.Error.WriteLine("usage: NetFrameworkSmoke <publicKey> <privateKey> <apiEndpoint> <wrEndpoint> <testHost>"); return 2; }
        int failures = 0;
        void Check(string name, bool ok, string detail = null) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail != null ? "  " + detail : "")); if (!ok) failures++; }

        Console.WriteLine("runtime: " + (Type.GetType("Mono.Runtime") != null ? "Mono" : ".NET Framework") + " " + Environment.Version + ", SDK build: " + typeof(GateKeeper).Assembly.Location);
        var gk = new GateKeeper(args[0], args[1], args[2], args[3], null, "10", "0", null, "0");
        string host = args[4]; string ua = "Net45Smoke/1.0"; string ip = "203.0.113.45";

        var rooms = gk.getRoomConfig();
        Check("rooms feed", rooms.Any(r => r.domain.Contains(host)), rooms.Count + " rooms");

        var first = gk.Validate(new Uri("https://" + host + "/tickets?smoke=1"), ua, "en-GB", ip);
        Check("first visit promoted", first.Action == "allow" && first.setCookie && first.responseID != null, first.Action + " token=" + first.token);

        var cookie = JsonConvert.DeserializeObject<CookieData>(first.cookieValue);
        var sig = cookie.tokens[0].signatures.FirstOrDefault();
        var room = gk.IsRoomMatch(host, "/tickets?smoke=1");
        Check("real hash validates locally (.NET Framework FIPS-safe SHA-256 path)", sig != null && gk.ValidateSignature(sig.sig, sig.gen, first.token, room).success);

        var second = gk.Validate(new Uri("https://" + host + "/tickets?smoke=2"), ua, "en-GB", ip, first.cookieValue);
        Check("second visit local", second.Action == "allow" && second.responseID == null);

        var queue = gk.Validate(new Uri("https://" + host + "/queue/x"), ua, "en-GB", ip);
        Check("countdown room redirects", queue.Action == "redirect" && queue.redirectUrl.StartsWith(args[3]), queue.redirectUrl);

        string requested = sig.gen.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var back = gk.Validate(new Uri("https://" + host + "/tickets?k=1&ch-id=" + first.token + "&ch-id-signature=" + sig.sig + "&ch-requested=" + Uri.EscapeDataString(requested) + "&ch-fresh=true"), ua, "en-GB", ip);
        Check("return from waiting room cleans url", back.Action == "redirect" && back.redirectUrl == "https://" + host + "/tickets?k=1" && back.setCookie, back.redirectUrl);

        var tampered = gk.Validate(new Uri("https://" + host + "/tickets?ch-id=tok0M7SBFAp9J8kK&ch-id-signature=deadbeef&ch-requested=garbage"), ua, "en-GB", ip);
        Check("tampered params never throw", tampered.Action == "redirect" || tampered.Action == "allow");

        try { new GateKeeper(new string('0', 64), args[1], args[2], args[3], null, "10", "0", null).getRoomConfig(); Check("bad key is a client error", false); }
        catch (CrowdhandlerApiException ex) { Check("bad key is a client error", ex.IsClientError, "HTTP " + ex.StatusCode); }

        try { new GateKeeper(args[0], args[1], "https://10.255.255.1", args[3], null, "1", "0", null).getRoomConfig(); Check("unroutable api is transient", false); }
        catch (CrowdhandlerApiException ex) { Check("unroutable api is transient", ex.IsTransient); }

        gk.RecordPerformance(first.responseID, 200, 42, 1.0);
        System.Threading.Thread.Sleep(1500);
        Check("performance sample did not throw", true);

        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURES");
        return failures == 0 ? 0 : 1;
    }
}
