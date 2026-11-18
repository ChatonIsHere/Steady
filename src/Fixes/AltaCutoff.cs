using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace Steady.Fixes;

// Alta's web services are gone, and every call the game still makes to them hangs until it times out (40 s and
// more). Steady cuts them off; nothing stands in for Alta. The calls that still ask it something are skipped with
// an empty answer (no public IP, no permission, no news or reports, no login refresh, analytics dropped), and any
// other request to Alta's hosts fails at once the way a timeout does, so the game takes its own timeout path
// without the wait. Each patch runs after other mods' and stands aside when one of them has already answered
// (TavernLib, TavernEssentials, AltaApiRemoval), so theirs win.
internal static class AltaCutoff
{
    private static readonly string[] DeadHosts = { "townshiptale.com", "execute-api.ap-southeast-2.amazonaws.com" };

    // The calls skipped outright: type, method, parameter count.
    private static readonly (string type, string method, int args)[] Calls =
    {
        ("Alta.Api.Client.HighLevel.UserApiClient", "RefreshLoginAsync", 0),
        ("Alta.Api.Client.HighLevel.ServicesApiClient", "GetPublicIpAddressAsync", 0),
        ("Alta.Api.Client.LowLevel.LowLevelApiClient", "EnqueueAnalyticsEvent", 1),
        ("Alta.Api.Client.HighLevel.StatusAPI", "GetStatus", 0),
        ("Alta.Api.Client.HighLevel.PlayerReportsClient", "ViewAllReports", 2),
        ("Alta.Api.Client.HighLevel.ServerApiClient", "HasPermissionsInServer", 3),
        // Area visits, challenges, unlocks, tutorials: each event posts to Alta on its own, then logs the failure
        // with its stack trace.
        ("ATT.Analytics.AltaAnalyticsDispatcher", "OnAnalyticsEvent", 1),
    };

    private static readonly Dictionary<MethodBase, object> answers = new();
    private static readonly List<MethodBase> patched = new();
    private static readonly HashSet<string> seen = new();
    private static int skipped, refused;

    public static int Skipped => skipped;
    public static int Refused => refused;

    public static void Apply()
    {
        if (!SteadyConfig.CutOffAlta) return;
        foreach (var (type, method, args) in Calls)
        {
            var target = AccessTools.TypeByName(type)?.GetMethods(AccessTools.allDeclared)
                .FirstOrDefault(m => m.Name == method && m.GetParameters().Length == args);
            if (target == null || !Patch(target, target.ReturnType == typeof(void) ? nameof(SkipVoid) : nameof(Skip))) continue;
            answers[target] = target.ReturnType == typeof(void) ? null : Nothing(target.ReturnType);
        }
        // Every request ends in the handler at the bottom of a client's chain; HttpClient's own entry points don't
        // all pass through one public method on Mono.
        var send = new[] { typeof(HttpRequestMessage), typeof(CancellationToken) };
        Patch(AccessTools.Method(typeof(HttpClientHandler), "SendAsync", send), nameof(Refuse));
        Patch(AccessTools.Method(typeof(HttpMessageInvoker), nameof(HttpMessageInvoker.SendAsync), send), nameof(Refuse));
        if (patched.Count > 0) SteadyMod.Active.Add("Alta cut off");
    }

    // For the startup line: which of these other mods patch too (they answer first; Steady stands aside).
    public static void Report()
    {
        var others = patched.SelectMany(m => HarmonyLib.Harmony.GetPatchInfo(m)?.Prefixes ?? Enumerable.Empty<Patch>())
            .Select(p => p.owner).Where(o => o != SteadyMod.HarmonyId)
            .Select(o => o.Contains(", Version=") ? o.Substring(o.LastIndexOf(':') + 1) : o) // MelonLoader's ids: "<assembly>:<mod>"
            .Distinct().ToList();
        if (others.Count > 0) SteadyMod.Active.Add($"Alta calls other mods answer left to them ({string.Join(", ", others)})");
    }

    private static bool Patch(MethodBase target, string prefix)
    {
        if (!SteadyMod.Patch(target, "CutOffAlta", typeof(AltaCutoff), prefix, priority: Priority.Last)) return false;
        patched.Add(target);
        return true;
    }

    // A finished task with nothing in it: no IP (0.0.0.0, what a failed lookup gives), false, an empty list.
    private static object Nothing(Type returns)
    {
        if (!returns.IsGenericType) return Task.CompletedTask;
        var value = returns.GetGenericArguments()[0];
        object result = value == typeof(IPAddress) ? IPAddress.Any
            : value.IsGenericType && value.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? Array.CreateInstance(value.GetGenericArguments()[0], 0)
            : value.IsValueType ? Activator.CreateInstance(value) : null;
        return typeof(Task).GetMethod(nameof(Task.FromResult)).MakeGenericMethod(value).Invoke(null, new[] { result });
    }

    private static bool Skip(MethodBase __originalMethod, ref object __result, bool __runOriginal)
    {
        if (!__runOriginal) return false; // another mod answered it
        __result = answers[__originalMethod];
        return SkipVoid(__originalMethod, true);
    }

    private static bool SkipVoid(MethodBase __originalMethod, bool __runOriginal)
    {
        if (!__runOriginal) return false;
        Interlocked.Increment(ref skipped);
        var name = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name;
        lock (seen)
            if (seen.Add(name)) SteadyMod.Log.Msg($"Skipped {name} (Alta's web services are gone).");
        return false;
    }

    private static bool Refuse(HttpRequestMessage request, ref Task<HttpResponseMessage> __result, bool __runOriginal)
    {
        if (!__runOriginal) return false;
        var uri = request?.RequestUri;
        if (uri == null || !uri.IsAbsoluteUri || !DeadHosts.Any(d => uri.Host.Equals(d, StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase)))
            return true;
        Interlocked.Increment(ref refused);
        // Once per endpoint, without ids or query strings.
        var path = uri.Host + "/" + string.Join("/", uri.AbsolutePath.Split('/').Where(p => p.Length > 0 && !char.IsDigit(p[0])));
        lock (seen)
            if (seen.Add(path)) SteadyMod.Log.Msg($"Refused {request.Method} {path} (Alta's web services are gone).");
        var failed = new TaskCompletionSource<HttpResponseMessage>();
        failed.SetException(new TaskCanceledException("Alta's web services are gone (Steady)"));
        __result = failed.Task;
        return false;
    }
}
