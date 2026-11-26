using DeltaVMap.Mission;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace DeltaVMap.HarnessTests;

public sealed class ReturnCompletionTest : IHarnessTest
{
    private const string Tag = "[dvmap-return-completion]";

    public string Name => "dvmap-return-completion";

    public int Run(HeadlessSession session)
    {
        CelestialSystem system = session.System;
        IParentBody? home = system.HomeBody;
        if (home is not Astronomical { BodyTemplate: { } homeTemplate }
            || home is not IOrbiter { Parent: { } star }
            || !system.All.TryGet("Mars", out Astronomical? target)
            || target is not IParentBody mars
            || target.BodyTemplate is not { } marsTemplate
            || DestinationRegistry.FindBody(marsTemplate) is not { } marsDestination
            || DestinationRegistry.FindBody(homeTemplate) is not { } homeDestination)
        {
            HarnessLog.Line($"{Tag} FAIL: requires the Sol registry with Earth and Mars.");
            return 1;
        }

        var plan = new MissionPlan
        {
            Destination = MissionDestination.Body,
            BodyId = mars.Id,
            Arrival = MissionArrival.Orbit,
            Trip = MissionTrip.Return,
        };
        var tree = new PartTree { Mission = plan };

        MissionProgress.Record(plan,
            new ArrivalEvent(ArrivalEventKind.Arrived, marsDestination, DestinationSituations.Orbiting, 10), mars, home.Id);
        MissionProgress.Record(plan,
            new ArrivalEvent(ArrivalEventKind.Left, marsDestination, DestinationSituations.InSoi, 20), star, home.Id);
        MissionProgress.Record(plan,
            new ArrivalEvent(ArrivalEventKind.Arrived, homeDestination, DestinationSituations.Orbiting, 30), home, home.Id);
        MissionSnapshot? returning = MissionReader.Read(tree);
        bool ok = Check("home orbit leaves the homeward route active",
            plan.LeftAt.HasValue && !plan.ReturnedAt.HasValue
            && MissionRoutes.DefaultRoute(returning, home.Id) == new MissionRouteGoal(home.Id, RouteEnd.Orbit, false, true));

        MissionProgress.Record(plan,
            new ArrivalEvent(ArrivalEventKind.Changed, homeDestination, DestinationSituations.Landed, 40), home, home.Id);
        MissionSnapshot? completed = MissionReader.Read(tree);
        ok &= Check("stock touchdown ends the homeward route",
            plan.ReturnedAt == new UniverseTime(40) && completed is { Completed: true }
            && MissionRoutes.DefaultRoute(completed, home.Id) == null);
        MissionKey before = MissionRoutes.KeyOf(tree, returning);
        MissionKey after = MissionRoutes.KeyOf(tree, completed);
        ok &= Check("touchdown clears only a mission selection",
            MissionRoutes.ShouldApply(before, after, false, true, true, true)
            && !MissionRoutes.ShouldApply(before, after, false, true, false, true));

        MissionTransfer.Retarget(plan, mars.Id);
        MissionSnapshot? retargeted = MissionReader.Read(tree);
        ok &= Check("stock retarget restores the outbound route",
            !plan.ReturnedAt.HasValue
            && MissionRoutes.DefaultRoute(retargeted, home.Id) == new MissionRouteGoal(mars.Id, RouteEnd.Orbit, true, false));
        return ok ? 0 : 1;
    }

    private static bool Check(string what, bool pass)
    {
        HarnessLog.Line($"{Tag} TEST {what} => {TestSupport.Verdict(pass)}");
        return pass;
    }
}
