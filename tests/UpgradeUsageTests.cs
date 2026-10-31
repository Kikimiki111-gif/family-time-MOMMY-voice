using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FamilyTimeVoice;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

static class Program
{
    static int passed;
    static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); passed++; }
    static void Throws(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("FAIL: " + name); }
    static async Task ThrowsAsync(Func<Task> action, string name) { try { await action(); } catch { Check(true, name); return; } throw new Exception("FAIL: " + name); }
    static string NewPath(string root, string name) { string directory = Path.Combine(root, name); Directory.CreateDirectory(directory); return Path.Combine(directory, "ledger.json"); }
    static decimal Voice(decimal seconds) { return seconds * 0.05m / 60m; }

    static void LedgerTests(string root)
    {
        string path = NewPath(root, "legacy");
        File.WriteAllText(path, "{\"Version\":1,\"Entries\":[{\"ReservationId\":\"old-dev\",\"SessionId\":\"old-1\",\"Development\":true,\"Status\":\"reported\",\"EstimatedUsd\":0.02026592},{\"ReservationId\":\"old-normal1\",\"SessionId\":\"old-2\",\"Status\":\"reported\",\"EstimatedUsd\":0.00665120},{\"ReservationId\":\"old-normal2\",\"SessionId\":\"old-3\",\"Status\":\"reported\",\"EstimatedUsd\":0.00687020}]}");
        var settings = new VoiceSettings(); var ledger = new UsageLedger(path);
        Check(ledger.TotalUsd == 0.03378732m && ledger.DevelopmentUsd == 0.02026592m, "Version1 historical Realtime totals survive additive upgrade");
        Check(settings.DevelopmentLimitUsd - ledger.DevelopmentUsd == 0.22973408m, "remaining development allowance preserves prior usage exactly");
        string id = ledger.BeginLiveSession(settings, true); ledger.BindLiveSession(id, "live_test");
        Check(ledger.SessionUsd == Voice(10), "Live starts with a closing-time reserve before connecting");
        ledger.UpdateLiveDuration(id, 12); ledger.UpdateLiveDuration(id, 15); ledger.UpdateLiveDuration(id, 13);
        Check(ledger.SessionUsd == Voice(15), "cumulative snapshots use monotonic max rather than 12+15+13");
        ledger.UpdateLiveClock(id, 20, settings);
        Check(ledger.SessionUsd == Voice(30), "idle and muted wall-clock time plus close headroom remain reserved");
        ledger.CompleteLiveSession(id, 15); decimal total = ledger.TotalUsd;
        ledger.CompleteLiveSession(id, 15); ledger.UpdateLiveDuration(id, 100); ledger.UpdateLiveClock(id, 100, settings);
        Check(ledger.TotalUsd == total && ledger.SessionUsd == Voice(15) && ledger.UnresolvedCount == 0, "final duration reconciles reserve once and ignores late snapshots");
        var reloaded = new UsageLedger(path);
        Check(reloaded.TotalUsd == total && reloaded.DevelopmentUsd == 0.02026592m + Voice(15), "legacy plus new duration charges survive restart");
        Check(JObject.Parse(File.ReadAllText(path))["Version"].Value<int>() == 1, "ledger upgrade preserves Version1 rather than resetting history");

        var missing = new UsageLedger(NewPath(root, "missing-final")); string missingId = missing.BeginLiveSession(settings);
        missing.UpdateLiveClock(missingId, 30, settings); decimal reserved = missing.TotalUsd;
        missing.CompleteLiveSession(missingId, null);
        Check(missing.TotalUsd == reserved && new UsageLedger(NewPath(root, "missing-final")).UnresolvedCount == 1, "unconfirmed socket closure retains conservative usage across restart");
        Throws(() => missing.UpdateLiveDuration(missingId, double.NaN), "invalid NaN duration rejected");
        Throws(() => missing.UpdateLiveDuration(missingId, -1), "negative duration rejected");
        missing.UpdateLiveDuration(missingId, 50);
        Throws(() => missing.CompleteLiveSession(missingId, 40), "final duration below a reported snapshot fails closed");

        var limited = new UsageLedger(NewPath(root, "limits")); var tiny = new VoiceSettings { SessionLimitUsd = 0.01m };
        string limitedId = limited.BeginLiveSession(tiny);
        Throws(() => limited.UpdateLiveClock(limitedId, 3, tiny), "close reserve triggers cutoff before actual voice spend reaches session limit");
        limited.CompleteLiveSession(limitedId, 3);
        Check(limited.TotalUsd == Voice(3) && limited.TotalUsd < tiny.SessionLimitUsd, "final reconciliation removes unused closing reserve");
        var cumulative = new VoiceSettings { CumulativeLimitUsd = 0.001m };
        Throws(() => new UsageLedger(NewPath(root, "cum")).BeginLiveSession(cumulative), "Live preflight honors cumulative cutoff");
        var dev = new VoiceSettings { DevelopmentLimitUsd = 0.001m };
        Throws(() => new UsageLedger(NewPath(root, "dev")).BeginLiveSession(dev, true), "Live preflight honors development cutoff");
        Throws(() => new UsageLedger(NewPath(root,"unknown-live")).BeginLiveSession(new VoiceSettings{LiveModel="unpriced-live"}),"an unknown Live model cannot inherit gpt-live-1 pricing");

        var transcription = new UsageLedger(NewPath(root, "transcription")); transcription.BeginSession(settings);
        transcription.ReserveTranscription("capture-1", 30, settings); transcription.BindTranscription("capture-1", "item-1");
        transcription.ReportTranscription("item-1", JObject.Parse("{\"type\":\"duration\",\"seconds\":12}"));
        Check(transcription.TotalUsd == 12m * 0.0045m / 60m, "opt-in gpt-transcribe duration is a separate priced charge");
        Check(!transcription.ReportTranscription("item-1", JObject.Parse("{\"type\":\"duration\",\"seconds\":12}")), "transcription item usage is deduplicated");
        Throws(() => transcription.ReserveTranscription(settings, 30, "unpriced-model"), "unpriced transcription fails before use");
        transcription.ReserveTranscription("clear-before-commit", 30, settings); decimal withPending = transcription.TotalUsd;
        transcription.ReleaseTranscription("clear-before-commit");
        Check(transcription.TotalUsd < withPending, "cleared uncommitted capture releases transcription reserve");
        transcription.ReserveTranscription("missing-usage", 30, settings); transcription.BindTranscription("missing-usage", "item-missing");
        Throws(() => transcription.ReportTranscription("item-missing", null), "missing paid transcription usage retains reserve and fails closed");

        var backend = new UsageLedger(NewPath(root, "backend")); string liveId = backend.BeginLiveSession(settings);
        Throws(() => backend.ReserveLiveBackend(liveId, "unpriced", 100, 100, settings), "delegation cannot start with an unpriced backend model");
        backend.LivePrices.BackendModels["offline-test-model"] = new BackendTokenPrice { Input = 2m, CachedInput = 0.5m, Output = 8m };
        backend.LivePrices.BackendModels["offline-test-model"].ToolUsdPerCall["offline-test-tool"] = 0.01m;
        string reservation = backend.ReserveLiveBackend(liveId, "offline-test-model", 1000, 1000, settings, new System.Collections.Generic.Dictionary<string,int>{{"offline-test-tool",2}}); backend.BindLiveBackend(reservation, "resp-1");
        var usage = JObject.Parse("{\"input_tokens\":100,\"input_tokens_details\":{\"cached_tokens\":20},\"output_tokens\":10}");
        backend.ReportLiveBackend(liveId, "resp-1", "offline-test-model", usage, new System.Collections.Generic.Dictionary<string,int>{{"offline-test-tool",2}});
        decimal backendTotal = backend.TotalUsd;
        Check(backendTotal == Voice(10) + (80m*2m+20m*0.5m+10m*8m)/1000000m + 0.02m, "delegated model cached tokens and tool costs counted separately from duration");
        Check(!backend.ReportLiveBackend(liveId,"resp-1","offline-test-model",usage), "delegated response_id is counted only once");
        string second = backend.ReserveLiveBackend(liveId,"offline-test-model",1000,1000,settings); backend.BindLiveBackend(second,"resp-2");
        Throws(() => backend.ReportLiveBackend(liveId,"resp-2","offline-test-model",usage,new System.Collections.Generic.Dictionary<string,int>{{"unknown-tool",1}}), "unknown delegated tool retains reservation and fails closed");
        Throws(() => backend.ReserveLiveBackend(liveId,"offline-test-model",100,100,settings,new System.Collections.Generic.Dictionary<string,int>{{"unknown-tool",1}}), "unpriced tool is also rejected before backend work is reserved");

        // Run the real installed 0.1.0 baseline's constructor/Save against this
        // fixture, not a serializer that merely resembles the old implementation.
        string repositoryRoot=AppContext.BaseDirectory;
        while(!File.Exists(Path.Combine(repositoryRoot,"FamilyTimeVoice.csproj"))) { var parent=Directory.GetParent(repositoryRoot);if(parent==null)throw new DirectoryNotFoundException("Cannot locate source root for the rollback fixture.");repositoryRoot=parent.FullName; }
        string oldDll=Path.Combine(repositoryRoot,".tools/upgrade-baseline-0.1.0/installed-FamilyTimeVoice.dll");
        if(!File.Exists(oldDll)||Environment.GetEnvironmentVariable("FTV_USE_SOURCE_BASELINE")=="1") oldDll=Path.Combine(repositoryRoot,"tests/LegacyLedgerBaseline/bin/LegacyLedgerBaseline/Release/net8.0/FamilyTimeVoice.LegacyLedgerBaseline.dll");
        if(!File.Exists(oldDll)) throw new FileNotFoundException("Original accounting fixture must be built before the rollback regression check.",oldDll);
        backend.CompleteLiveSession(liveId,30); decimal beforeOld=backend.TotalUsd,devBeforeOld=backend.DevelopmentUsd;
        var oldAssembly=System.Reflection.Assembly.LoadFrom(oldDll);
        var oldType=oldAssembly.GetType("FamilyTimeVoice.UsageLedger",true);
        object oldLedger=Activator.CreateInstance(oldType,new object[]{NewPath(root,"backend"),null});
        Check((decimal)oldType.GetProperty("TotalUsd").GetValue(oldLedger)==beforeOld,"actual old0.1 DLL reads all new Live/backend costs through legacy mirrors");
        Check(JObject.Parse(File.ReadAllText(NewPath(root,"backend")))["LiveSessions"]==null,"actual old0.1 save exercises dropping new metadata fields");
        var afterOld=new UsageLedger(NewPath(root,"backend"));
        Check(afterOld.TotalUsd==beforeOld&&afterOld.DevelopmentUsd==devBeforeOld,"old-version roundtrip cannot erase new spend or double-count it on upgrade");
        var oldSettingsType=oldAssembly.GetType("FamilyTimeVoice.VoiceSettings",true);object oldSettings=Activator.CreateInstance(oldSettingsType);
        oldSettingsType.GetProperty("CumulativeLimitUsd").SetValue(oldSettings,beforeOld);
        Throws(()=>oldType.GetMethod("BeginSession").Invoke(oldLedger,new object[]{oldSettings,false}),"actual old0.1 cutoff still honors Live charges after rollback");
    }

    sealed class FakeHandler : HttpMessageHandler
    {
        public int Requests; public string LastBody = ""; public bool SecretInHeader; public bool Fail; public bool Forbidden; public bool DiagnosticError; public bool UnsafeDiagnostic; public bool Oversized; public int DelayMs; public string LastUri = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++; LastUri=request.RequestUri.AbsoluteUri; LastBody=await request.Content.ReadAsStringAsync(); SecretInHeader=request.Headers.Authorization?.Parameter=="offline-test-not-a-real-key";
            if(DelayMs>0) await Task.Delay(DelayMs,cancellationToken);
            if(Fail) throw new HttpRequestException("mock network loss");
            if(UnsafeDiagnostic) return new HttpResponseMessage(HttpStatusCode.BadRequest){Content=new StringContent("{\"error\":{\"code\":\"offline-test-not-a-real-key\",\"param\":\"offline-test-not-a-real-key\"}}")};
            if(DiagnosticError) return new HttpResponseMessage(HttpStatusCode.BadRequest){Content=new StringContent("{\"error\":{\"code\":\"unsupported_value\",\"param\":\"model\",\"message\":\"offline-sensitive-message-never-save\"}}")};
            if(Forbidden) return new HttpResponseMessage(HttpStatusCode.Forbidden){Content=new StringContent("never exposed provider body")};
            if(Oversized) return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(new string('x',70000))};
            return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("{\"object\":\"audio.voice\",\"type\":\"prompt\",\"id\":\"voice_offline_"+Requests+"\",\"created_at\":1793450000}")};
        }
    }
    static async Task CatalogTests(string root)
    {
        string path=Path.Combine(root,"voices.json"); var handler=new FakeHandler();
        using(var catalog=new VoiceDesignCatalog(path,handler))
        {
            Check(handler.Requests==0 && catalog.Candidates.Count==3,"constructing catalog never creates voices or opens a session");
            Check(catalog.Candidates.All(c=>c.Description.Contains("adult feminine")&&!c.Description.Contains("Design A")),"A/B/C are full standalone original adult descriptions");
            var a=await catalog.CreateAsync("A","offline-test-not-a-real-key");
            var repeated=await catalog.CreateAsync("A","offline-test-not-a-real-key");
            Check(handler.Requests==1&&a.VoiceId==repeated.VoiceId,"repeated voice selection reuses the saved ID without regeneration");
            var body=JObject.Parse(handler.LastBody);
            Check(handler.LastUri=="https://api.openai.com/v1/audio/voices"&&(string)body["type"]=="prompt"&&(string)body["model"]=="auto","voice request uses only the intended official prompt-to-voice endpoint/schema");
            Check(handler.SecretInHeader&&!handler.LastBody.Contains("offline-test-not-a-real-key")&&!File.ReadAllText(path).Contains("offline-test-not-a-real-key"),"credential is isolated to Authorization, absent from request JSON and metadata");
            Check(body["script_hint"]==null&&catalog.Candidates.All(c=>c.ScriptHint==VoiceDesignCatalog.ComparisonPassage),"creation omits optional short script_hint while auditions retain their shared passage");
            await Task.WhenAll(catalog.CreateAsync("B","offline-test-not-a-real-key"),catalog.CreateAsync("B","offline-test-not-a-real-key"));
            await catalog.CreateAsync("C","offline-test-not-a-real-key");
            Check(handler.Requests==3&&catalog.Saved.Count==3,"concurrent requests and three candidates create at most three saved voices");
            await ThrowsAsync(()=>catalog.CreateAsync("D","offline-test-not-a-real-key"),"fourth candidate is rejected before any request");
        }
        var restarted=new FakeHandler(); using(var catalog=new VoiceDesignCatalog(path,restarted)){await catalog.CreateAsync("A",null);Check(restarted.Requests==0,"saved voice cache survives restart and needs no key to reuse metadata");}
        var failure=new FakeHandler{Fail=true}; string failedPath=Path.Combine(root,"uncertain-voices.json");
        using(var catalog=new VoiceDesignCatalog(failedPath,failure))
        {
            await ThrowsAsync(()=>catalog.CreateAsync("A","offline-test-not-a-real-key"),"uncertain network creation is recorded without revealing key");
            await ThrowsAsync(()=>catalog.CreateAsync("A","offline-test-not-a-real-key"),"uncertain creation cannot automatically generate a duplicate");
            Check(failure.Requests==1,"uncertain response blocks duplicate POSTs");
        }
        var forbidden=new FakeHandler{Forbidden=true}; using(var catalog=new VoiceDesignCatalog(Path.Combine(root,"forbidden-voices.json"),forbidden))
        {await ThrowsAsync(()=>catalog.CreateAsync("A","offline-test-not-a-real-key"),"missing voice permission is actionable without exposing provider body");Check(catalog.GetSaved("A").CreationStatus=="rejected","explicit rejected request is distinguished from uncertain created voice");}
        var diagnostic=new FakeHandler{DiagnosticError=true};string diagnosticPath=Path.Combine(root,"diagnostic-voices.json");using(var catalog=new VoiceDesignCatalog(diagnosticPath,diagnostic))
        {
            await ThrowsAsync(()=>catalog.CreateAsync("A","offline-test-not-a-real-key"),"safe voice diagnostics keep a rejected request rejected");
            var record=catalog.GetSaved("A");Check(record.LastHttpStatus==400&&record.LastErrorCode=="unsupported_value"&&record.LastErrorParam=="model","only HTTP status and allowlisted code/parameter are retained");
            Check(!File.ReadAllText(diagnosticPath).Contains("offline-sensitive-message-never-save"),"voice provider error message is excluded from persistent diagnostics");
        }
        var unsafeDiagnostic=new FakeHandler{UnsafeDiagnostic=true};string unsafePath=Path.Combine(root,"unsafe-diagnostic.json");using(var catalog=new VoiceDesignCatalog(unsafePath,unsafeDiagnostic))
        {
            await ThrowsAsync(()=>catalog.CreateAsync("A","offline-test-not-a-real-key"),"unknown diagnostic identifiers do not bypass safe error handling");
            Check(!File.ReadAllText(unsafePath).Contains("offline-test-not-a-real-key")&&catalog.GetSaved("A").LastErrorCode=="unrecognized"&&catalog.GetSaved("A").LastErrorParam=="unrecognized","identifier-shaped secret values are excluded by the value allowlist");
        }
        var firstHandler=new FakeHandler{DelayMs=150};var secondHandler=new FakeHandler();string concurrentPath=Path.Combine(root,"two-catalogs.json");
        using(var first=new VoiceDesignCatalog(concurrentPath,firstHandler))using(var second=new VoiceDesignCatalog(concurrentPath,secondHandler))
        {
            var voices=await Task.WhenAll(first.CreateAsync("A","offline-test-not-a-real-key"),second.CreateAsync("A","offline-test-not-a-real-key"));
            Check(firstHandler.Requests+secondHandler.Requests==1&&voices[0].VoiceId==voices[1].VoiceId,"exclusive catalog file lock prevents duplicate requests across catalog instances");
            var external=new FakeHandler();using(var third=new VoiceDesignCatalog(concurrentPath,external)){await third.CreateAsync("B","offline-test-not-a-real-key");}
            second.Reload();Check(second.GetSaved("B")!=null,"explicit reload exposes externally created voice metadata without regeneration");
        }
        var oversized=new FakeHandler{Oversized=true};using(var catalog=new VoiceDesignCatalog(Path.Combine(root,"oversized-voices.json"),oversized))
        {await ThrowsAsync(()=>catalog.CreateAsync("A","offline-test-not-a-real-key"),"voice metadata response is bounded before full buffering");Check(catalog.GetSaved("A").CreationStatus=="uncertain","malformed or oversized metadata cannot trigger automatic regeneration");}
    }
    static async Task Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"FamilyTimeVoiceUpgradeTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        LedgerTests(root); await CatalogTests(root);
        Console.WriteLine(passed+" upgrade usage/voice checks passed; no network or paid API calls. Fixtures: "+root);
    }
}
