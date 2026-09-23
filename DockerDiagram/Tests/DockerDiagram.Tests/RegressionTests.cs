using DockerDiagram.Diagram;
using DockerDiagram.Infrastructure;
using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using Docker.DotNet.Models;
using System.IO;

namespace DockerDiagram.Tests;

public sealed class RegressionTests
{
    private static readonly (string Name, Action Run)[] Checks =
    {
    ("empty graph", CheckEmptyGraph),
    ("tree and ranks", CheckTreeAndRanks),
    ("forest", CheckForest),
    ("diamond DAG", CheckDiamondDag),
    ("cycle condensation ranks", CheckCycleCondensationRanks),
    ("network set relations", CheckNetworkSetRelations),
    ("display dependency direction", CheckDisplayDependencyDirection),
    ("proxy web db first row and volume shelf", CheckProxyWebDbFirstRowAndVolumeShelf),
    ("tidy tree first-row alignment", CheckTidyTreeFirstRowAlignment),
    ("tidy tree contour separation", CheckTidyTreeContourSeparation),
    ("tidy forest footprint spacing", CheckTidyForestFootprintSpacing),
    ("tidy tree variable depth sizes", CheckTidyTreeVariableDepthSizes),
    ("Sugiyama diamond layering", CheckSugiyamaDiamondLayering),
    ("Sugiyama crossing reduction", CheckSugiyamaCrossingReduction),
    ("Sugiyama long-edge dummy", CheckSugiyamaLongEdgeDummy),
    ("Sugiyama cycle normalization", CheckSugiyamaCycleNormalization),
    ("Sugiyama dense DAG stability", CheckSugiyamaDenseDagStability),
    ("volume reverse staircase and rank gap", CheckVolumeReverseStaircaseAndRankGap),
    ("volume shelf follows all component containers", CheckVolumeBranchPushesLowerSibling),
    ("volume stays under connector inside shared network", CheckVolumeInsideSharedNetwork),
    ("volume falls back below network", CheckVolumeOutsideNetworkFallback),
    ("volume requires an actual descendant", CheckVolumeRequiresActualDescendant),
    ("volume respects overlapping network intersection", CheckVolumeOverlappingNetworkIntersection),
    ("shared volume validates every owner branch", CheckSharedVolumeValidatesEveryOwnerBranch),
    ("external network shelves remain component-local", CheckExternalShelvesRemainComponentLocal),
    ("external volume shelves preserve owner ranks", CheckExternalVolumeShelvesPreserveOwnerRanks),
    ("external volume is removed from inline envelope", CheckExternalVolumeExcludedFromInlineEnvelope),
    ("volume envelope is reserved at component anchor", CheckVolumeEnvelopeSeparatesTreeBranches),
    ("volume large-count spacing", CheckVolumeLargeCountSpacing),
    ("shared and orphan volume planning", CheckSharedAndOrphanVolumePlanning),
    ("network identical membership rings", CheckNetworkIdenticalMembershipRings),
    ("network strict containment", CheckNetworkStrictContainment),
    ("network partial overlap and headers", CheckNetworkPartialOverlapAndHeaders),
    ("network disjoint bounds", CheckNetworkDisjointBounds),
    ("swarm connection policy", CheckSwarmConnectionPolicy),
    ("swarm task topology", CheckSwarmTaskTopology),
    ("swarm cluster state", CheckSwarmClusterState),
    ("swarm lifecycle options", CheckSwarmLifecycleOptions),
    ("swarm resource filter", CheckSwarmResourceFilter),
    ("swarm setup presentation", CheckSwarmSetupPresentation),
    ("swarm advertise address discovery", CheckSwarmAdvertiseAddressDiscovery),
    ("swarm initialization environment defaults", CheckSwarmInitializationEnvironmentDefaults),
    ("swarm join command", CheckSwarmJoinCommand),
    ("swarm target connection options", CheckSwarmTargetConnectionOptions),
    ("swarm join verification", CheckSwarmJoinVerification),
    ("swarm leave policy", SwarmRecoveryChecks.LeavePolicy),
    ("swarm leave response recovery", SwarmRecoveryChecks.LeaveExecution),
    ("swarm leave changed confirmation", SwarmRecoveryChecks.LeaveChangesBeforeExecution),
    ("swarm join strict identity", SwarmRecoveryChecks.JoinIdentity),
    ("swarm setup recovery controls", SwarmRecoveryChecks.SetupRecoveryControls),
    ("swarm API lifecycle HTTP", SwarmApiChecks.Lifecycle),
    ("swarm API preflight and cancellation", SwarmApiChecks.GuardsAndCancellation),
    ("swarm setup menu access", SwarmApiChecks.MenuAccess),
    ("read-only cluster role bindings", CheckReadOnlyClusterRoleBindings),
    ("swarm Join button end-to-end", SwarmRecoveryChecks.JoinButtonFlow)
};

    public static IEnumerable<object[]> Cases =>
        Checks.Select(check => new object[] { check.Name });

    [Theory]
    [MemberData(nameof(Cases))]
    public void RunRegression(string name) =>
        Checks.Single(check => check.Name == name).Run();

    static void CheckEmptyGraph()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            Array.Empty<string>(),
            Array.Empty<ComposeLayoutEdge>());

        Equal(ComposeLayoutGraphKind.Empty, graph.Kind, "empty kind");
        Equal(0, graph.Ranks.Count, "empty ranks");
    }

    static void CheckTreeAndRanks()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            new[] { "root", "left", "right", "leaf" },
            new[]
            {
            new ComposeLayoutEdge("root", "left"),
            new ComposeLayoutEdge("root", "right"),
            new ComposeLayoutEdge("left", "leaf")
            });

        Equal(ComposeLayoutGraphKind.Tree, graph.Kind, "tree kind");
        Equal(0, graph.RankByNode["root"], "root rank");
        Equal(1, graph.RankByNode["left"], "left rank");
        Equal(1, graph.RankByNode["right"], "right rank");
        Equal(2, graph.RankByNode["leaf"], "leaf rank");
        Equal(2, graph.ChildrenByNode["root"].Count, "root out-degree");
    }

    static void CheckForest()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            new[] { "a", "b", "isolated" },
            new[] { new ComposeLayoutEdge("a", "b") });

        Equal(ComposeLayoutGraphKind.Forest, graph.Kind, "forest kind");
        Equal(2, graph.ConnectedComponents.Count, "forest components");
    }

    static void CheckDiamondDag()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            new[] { "root", "left", "right", "sink" },
            new[]
            {
            new ComposeLayoutEdge("root", "left"),
            new ComposeLayoutEdge("root", "right"),
            new ComposeLayoutEdge("left", "sink"),
            new ComposeLayoutEdge("right", "sink")
            });

        Equal(ComposeLayoutGraphKind.DirectedAcyclicGraph, graph.Kind, "DAG kind");
        Equal(2, graph.ParentsByNode["sink"].Count, "shared child in-degree");
        Equal(2, graph.RankByNode["sink"], "longest-path rank");
        Equal(4, graph.Edges.Count, "all original edges preserved");
    }

    static void CheckCycleCondensationRanks()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            new[] { "a", "b", "tail" },
            new[]
            {
            new ComposeLayoutEdge("a", "b"),
            new ComposeLayoutEdge("b", "a"),
            new ComposeLayoutEdge("b", "tail")
            });

        Equal(ComposeLayoutGraphKind.Cyclic, graph.Kind, "cycle kind");
        True(graph.CycleNodeIds.Contains("a"), "a must be a cycle member");
        True(graph.CycleNodeIds.Contains("b"), "b must be a cycle member");
        True(!graph.CycleNodeIds.Contains("tail"), "downstream node must not be marked cyclic");
        Equal(graph.RankByNode["a"], graph.RankByNode["b"], "SCC members share a rank");
        Equal(graph.RankByNode["b"] + 1, graph.RankByNode["tail"], "condensation DAG rank");
    }

    static void CheckNetworkSetRelations()
    {
        Equal(
            ComposeNetworkRelationKind.Disjoint,
            ComposeLayoutGraphAnalyzer.ClassifyNetworkRelation(new[] { "a" }, new[] { "b" }),
            "disjoint");
        Equal(
            ComposeNetworkRelationKind.Identical,
            ComposeLayoutGraphAnalyzer.ClassifyNetworkRelation(new[] { "a", "b" }, new[] { "B", "A" }),
            "identical");
        Equal(
            ComposeNetworkRelationKind.LeftContainsRight,
            ComposeLayoutGraphAnalyzer.ClassifyNetworkRelation(new[] { "a", "b" }, new[] { "a" }),
            "left contains");
        Equal(
            ComposeNetworkRelationKind.RightContainsLeft,
            ComposeLayoutGraphAnalyzer.ClassifyNetworkRelation(new[] { "a" }, new[] { "a", "b" }),
            "right contains");
        Equal(
            ComposeNetworkRelationKind.PartialOverlap,
            ComposeLayoutGraphAnalyzer.ClassifyNetworkRelation(
                new[] { "a", "b" },
                new[] { "b", "c" }),
            "partial overlap");
    }

    static void CheckDisplayDependencyDirection()
    {
        string[] services = { "proxy", "web1", "web2", "web3", "db" };
        var dependsOn = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["proxy"] = new() { "web1", "web2", "web3" },
            ["web1"] = new() { "db" },
            ["web2"] = new() { "db" },
            ["web3"] = new() { "db" }
        };
        IReadOnlyList<ComposeLayoutEdge> edges =
            ComposeLayoutGraphAnalyzer.CreateDisplayDependencyEdges(services, dependsOn);
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(services, edges);

        Equal(0, graph.RankByNode["proxy"], "proxy is the first display rank");
        Equal(1, graph.RankByNode["web1"], "web is the second display rank");
        Equal(2, graph.RankByNode["db"], "db is the final display rank");
        True(edges.Contains(new ComposeLayoutEdge("proxy", "web1")),
            "proxy points toward web in the display graph");
        True(edges.Contains(new ComposeLayoutEdge("web1", "db")),
            "web points toward db in the display graph");
    }

    static void CheckProxyWebDbFirstRowAndVolumeShelf()
    {
        string[] services = { "proxy", "web1", "web2", "web3", "db" };
        var dependsOn = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["proxy"] = new() { "web1", "web2", "web3" },
            ["web1"] = new() { "db" },
            ["web2"] = new() { "db" },
            ["web3"] = new() { "db" }
        };
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            services,
            ComposeLayoutGraphAnalyzer.CreateDisplayDependencyEdges(services, dependsOn));
        var components = services.ToDictionary(
            id => id,
            id => graph.ConnectedComponentByNode[id],
            StringComparer.OrdinalIgnoreCase);
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            graph.RankByNode,
            services.ToDictionary(id => id, _ => 50d),
            new[]
            {
            new ComposeVolumeLayoutItem("web1Volume", 80, 40, new[] { "web1" }),
            new ComposeVolumeLayoutItem("web2Volume", 80, 40, new[] { "web2" }),
            new ComposeVolumeLayoutItem("web3Volume", 80, 40, new[] { "web3" }),
            new ComposeVolumeLayoutItem("dbVolume", 80, 40, new[] { "db" })
            },
            10,
            inlineVolumeIds: null,
            components);
        ComposeSugiyamaLayoutResult serviceLayout = ComposeSugiyamaLayoutEngine.Arrange(
            graph,
            services.Select(id => new ComposeSugiyamaNode(
                id,
                80,
                plan.ReservedBreadthByService[id])),
            0, 0, 90, 10);
        var slots = services.Select(id => new ComposeVolumeServiceSlot(
            id,
            graph.RankByNode[id],
            serviceLayout.Positions[id].Depth,
            serviceLayout.Positions[id].Breadth,
            80,
            50)).ToArray();
        ComposeVolumeLayoutResult layout = ComposeVolumeLayoutEngine.Arrange(
            plan, slots, 0, 0, 20, 20, 30, 80);

        Near(layout.ServicePositions["proxy"].Breadth, layout.ServicePositions["web1"].Breadth,
            0.001, "proxy and first web share first row");
        Near(layout.ServicePositions["proxy"].Breadth, layout.ServicePositions["db"].Breadth,
            0.001, "db remains on first row instead of being centered");
        True(layout.ServicePositions["web2"].Breadth > layout.ServicePositions["web1"].Breadth,
            "second web is below first web");
        True(layout.ServicePositions["web3"].Breadth > layout.ServicePositions["web2"].Breadth,
            "third web is below second web");

        double containerEnd = slots.Max(slot => slot.Breadth + slot.BreadthSize);
        True(layout.VolumePositions.Values.All(position => position.Breadth >= containerEnd + 10 - 0.001),
            "every volume starts below all containers");
        True(layout.VolumePositions["web1Volume"].Depth > layout.VolumePositions["web2Volume"].Depth &&
             layout.VolumePositions["web2Volume"].Depth > layout.VolumePositions["web3Volume"].Depth,
            "web volumes form the requested upper-right to lower-left staircase");

        var network = new ComposeVolumeNetworkRegion(
            "combined", -20, -20, 500, 220, services);
        ComposeVolumeServiceSlot[] shiftedSlots = slots
            .Select(service => service with
            {
                Depth = layout.ServicePositions[service.Id].Depth,
                Breadth = layout.ServicePositions[service.Id].Breadth
            })
            .ToArray();
        ComposeVolumeLayoutResult external = ComposeVolumeLayoutEngine.ResolveNetworkAwarePlacement(
            plan,
            shiftedSlots,
            layout,
            new[] { network },
            Descendants(
                ("proxy", new[] { "web1", "web2", "web3", "db" }),
                ("web1", new[] { "db" }),
                ("web2", new[] { "db" }),
                ("web3", new[] { "db" }),
                ("db", Array.Empty<string>())),
            60);
        True(external.ExternalVolumeIds.Count == 4, "non-fitting shelf moves below the network");
        True(external.VolumePositions["web1Volume"].Depth >
             external.VolumePositions["web2Volume"].Depth &&
             external.VolumePositions["web2Volume"].Depth >
             external.VolumePositions["web3Volume"].Depth,
            "external fallback preserves the web volume staircase");
        Near(external.VolumePositions["dbVolume"].Breadth,
             external.VolumePositions["web1Volume"].Breadth,
             0.001,
             "DB one-level group and web three-level group start on the same shelf row");
        True(external.VolumePositions["web2Volume"].Breadth >
             external.VolumePositions["web1Volume"].Breadth + 40 &&
             external.VolumePositions["web3Volume"].Breadth >
             external.VolumePositions["web2Volume"].Breadth + 40,
            "web-owned volumes alone form the three-level group");
        True(external.VolumePositions["dbVolume"].Depth >
             external.VolumePositions["web1Volume"].Depth &&
             external.VolumePositions["web1Volume"].Depth >
             external.VolumePositions["web2Volume"].Depth &&
             external.VolumePositions["web2Volume"].Depth >
             external.VolumePositions["web3Volume"].Depth,
            "DB group is right of the web-owned staircase");
        ComposeVolumeServiceSlot[] finalSlots = shiftedSlots
            .Select(service => service with
            {
                Depth = external.ServicePositions[service.Id].Depth,
                Breadth = external.ServicePositions[service.Id].Breadth
            })
            .ToArray();
        True(external.VolumePositions["dbVolume"].Depth >=
             external.ServicePositions["db"].Depth + 80 - 0.001,
            "DB volume is anchored at the DB container's lower-right");
        double webOwnerRight = new[] { "web1", "web2", "web3" }
            .Max(id => external.ServicePositions[id].Depth + 80);
        True(external.VolumePositions["web1Volume"].Depth >= webOwnerRight - 0.001 &&
             external.VolumePositions["web2Volume"].Depth >= webOwnerRight - 0.001 &&
             external.VolumePositions["web3Volume"].Depth >= webOwnerRight - 0.001,
            "web volume group is anchored at the web containers' lower-right");
        foreach ((string volumeId, ComposeVolumeAxisPosition volumePosition) in external.VolumePositions)
        {
            ComposeVolumeLayoutItem volume = plan.Volumes[volumeId];
            foreach (ComposeVolumeServiceSlot service in finalSlots)
            {
                bool overlapsContainerDepth =
                    volumePosition.Depth < service.Depth + service.DepthSize &&
                    volumePosition.Depth + volume.DepthSize > service.Depth;
                True(!overlapsContainerDepth,
                    $"portfolio volume '{volumeId}' must not sit below service '{service.Id}'");
            }
        }
    }

    static void CheckTidyTreeFirstRowAlignment()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            new[] { "root", "left", "middle", "right" },
            new[]
            {
            new ComposeLayoutEdge("root", "left"),
            new ComposeLayoutEdge("root", "middle"),
            new ComposeLayoutEdge("root", "right")
            });
        ComposeTidyTreeLayoutResult layout = ComposeTidyTreeLayoutEngine.Arrange(
            graph,
            graph.OrderedNodeIds.Select(id => new ComposeTidyTreeNode(id, 100, 60)),
            depthOrigin: 10,
            breadthOrigin: 20,
            depthGap: 80,
            siblingGap: 30,
            forestGap: 100);

        Near(layout.Positions["left"].Breadth, layout.Positions["root"].Breadth, 0.001,
            "parent and first child share the first row");
        True(layout.Positions["middle"].Breadth > layout.Positions["left"].Breadth,
            "second child is placed below the first row");
        True(layout.Positions["right"].Breadth > layout.Positions["middle"].Breadth,
            "third child is placed below the second child");
        Equal(10d, layout.Positions["root"].Depth, "root depth");
        Equal(190d, layout.Positions["left"].Depth, "child depth");
    }

    static void CheckTidyTreeContourSeparation()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            new[] { "root", "wide", "narrow", "w1", "w2", "w3", "n1" },
            new[]
            {
            new ComposeLayoutEdge("root", "wide"),
            new ComposeLayoutEdge("root", "narrow"),
            new ComposeLayoutEdge("wide", "w1"),
            new ComposeLayoutEdge("wide", "w2"),
            new ComposeLayoutEdge("wide", "w3"),
            new ComposeLayoutEdge("narrow", "n1")
            });
        const double breadthSize = 50;
        const double siblingGap = 25;
        ComposeTidyTreeLayoutResult layout = ComposeTidyTreeLayoutEngine.Arrange(
            graph,
            graph.OrderedNodeIds.Select(id => new ComposeTidyTreeNode(id, 90, breadthSize)),
            depthOrigin: 0,
            breadthOrigin: 0,
            depthGap: 60,
            siblingGap,
            forestGap: 100);

        foreach (IReadOnlyList<string> rank in graph.Ranks)
        {
            var intervals = rank
                .Select(id => (
                    Id: id,
                    Start: layout.Positions[id].Breadth,
                    End: layout.Positions[id].Breadth + breadthSize))
                .OrderBy(interval => interval.Start)
                .ToArray();
            for (int index = 1; index < intervals.Length; index++)
            {
                True(
                    intervals[index].Start - intervals[index - 1].End >= siblingGap - 0.001,
                    $"{intervals[index - 1].Id} and {intervals[index].Id} contour gap");
            }
        }
    }

    static void CheckTidyForestFootprintSpacing()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            new[] { "r1", "a", "b", "r2", "c", "d", "e" },
            new[]
            {
            new ComposeLayoutEdge("r1", "a"),
            new ComposeLayoutEdge("r1", "b"),
            new ComposeLayoutEdge("r2", "c"),
            new ComposeLayoutEdge("c", "d"),
            new ComposeLayoutEdge("c", "e")
            });
        const double breadthSize = 40;
        const double forestGap = 120;
        ComposeTidyTreeLayoutResult layout = ComposeTidyTreeLayoutEngine.Arrange(
            graph,
            graph.OrderedNodeIds.Select(id => new ComposeTidyTreeNode(id, 80, breadthSize)),
            depthOrigin: 0,
            breadthOrigin: 15,
            depthGap: 70,
            siblingGap: 20,
            forestGap);

        string[] firstTree = { "r1", "a", "b" };
        string[] secondTree = { "r2", "c", "d", "e" };
        double firstEnd = firstTree.Max(id => layout.Positions[id].Breadth + breadthSize);
        double secondStart = secondTree.Min(id => layout.Positions[id].Breadth);
        True(secondStart - firstEnd >= forestGap - 0.001, "forest footprint gap");
        Near(15, layout.BreadthStart, 0.001, "forest breadth origin");
    }

    static void CheckTidyTreeVariableDepthSizes()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            new[] { "root", "childA", "childB", "leaf" },
            new[]
            {
            new ComposeLayoutEdge("root", "childA"),
            new ComposeLayoutEdge("root", "childB"),
            new ComposeLayoutEdge("childA", "leaf")
            });
        var sizes = new[]
        {
        new ComposeTidyTreeNode("root", 70, 50),
        new ComposeTidyTreeNode("childA", 120, 80),
        new ComposeTidyTreeNode("childB", 90, 40),
        new ComposeTidyTreeNode("leaf", 60, 55)
    };
        ComposeTidyTreeLayoutResult layout = ComposeTidyTreeLayoutEngine.Arrange(
            graph,
            sizes,
            depthOrigin: 5,
            breadthOrigin: 10,
            depthGap: 35,
            siblingGap: 15,
            forestGap: 80);

        Near(5, layout.Positions["root"].Depth, 0.001, "rank zero depth");
        Near(110, layout.Positions["childA"].Depth, 0.001, "rank one depth");
        Near(265, layout.Positions["leaf"].Depth, 0.001, "rank two uses largest prior node");
    }

    static void CheckSugiyamaDiamondLayering()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            new[] { "root", "left", "right", "sink" },
            new[]
            {
            new ComposeLayoutEdge("root", "left"),
            new ComposeLayoutEdge("root", "right"),
            new ComposeLayoutEdge("left", "sink"),
            new ComposeLayoutEdge("right", "sink")
            });
        var sizes = new[]
        {
        new ComposeSugiyamaNode("root", 70, 80),
        new ComposeSugiyamaNode("left", 110, 60),
        new ComposeSugiyamaNode("right", 90, 100),
        new ComposeSugiyamaNode("sink", 75, 70)
    };
        ComposeSugiyamaLayoutResult layout = ComposeSugiyamaLayoutEngine.Arrange(
            graph,
            sizes,
            depthOrigin: 10,
            breadthOrigin: 20,
            depthGap: 40,
            siblingGap: 30);

        Equal(0, layout.LayerByNode["root"], "diamond root layer");
        Equal(1, layout.LayerByNode["left"], "diamond left layer");
        Equal(1, layout.LayerByNode["right"], "diamond right layer");
        Equal(2, layout.LayerByNode["sink"], "diamond sink layer");
        Near(10, layout.Positions["root"].Depth, 0.001, "diamond rank zero depth");
        Near(120, layout.Positions["left"].Depth, 0.001, "diamond rank one depth");
        Near(270, layout.Positions["sink"].Depth, 0.001, "diamond variable rank depth");
        string firstMiddleId = layout.OrderedLayers[1][0];
        Near(layout.Positions["root"].Breadth, layout.Positions[firstMiddleId].Breadth, 0.001,
            "DAG first node in each rank shares the first row");
        Near(layout.Positions["root"].Breadth, layout.Positions["sink"].Breadth, 0.001,
            "single sink remains on the first row instead of being centered");

        double leftEnd = layout.Positions["left"].Breadth + 60;
        double rightStart = layout.Positions["right"].Breadth;
        if (layout.OrderedLayers[1][0] == "right")
        {
            leftEnd = layout.Positions["right"].Breadth + 100;
            rightStart = layout.Positions["left"].Breadth;
        }
        True(rightStart - leftEnd >= 30 - 0.001, "diamond sibling spacing");
    }

    static void CheckSugiyamaCrossingReduction()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            new[] { "a", "b", "c", "d", "sink" },
            new[]
            {
            new ComposeLayoutEdge("a", "d"),
            new ComposeLayoutEdge("b", "c"),
            new ComposeLayoutEdge("a", "sink"),
            new ComposeLayoutEdge("b", "sink")
            });
        ComposeSugiyamaLayoutResult layout = ComposeSugiyamaLayoutEngine.Arrange(
            graph,
            graph.OrderedNodeIds.Select(id => new ComposeSugiyamaNode(id, 80, 50)),
            depthOrigin: 0,
            breadthOrigin: 0,
            depthGap: 50,
            siblingGap: 20);

        True(layout.InitialCrossingCount > 0, "crossing example must start crossed");
        True(
            layout.FinalCrossingCount < layout.InitialCrossingCount,
            "barycenter sweeps must reduce crossings");
        Equal(0L, layout.FinalCrossingCount, "crossing example final count");
    }

    static void CheckSugiyamaLongEdgeDummy()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            new[] { "a", "b", "c" },
            new[]
            {
            new ComposeLayoutEdge("a", "b"),
            new ComposeLayoutEdge("b", "c"),
            new ComposeLayoutEdge("a", "c")
            });
        ComposeSugiyamaLayoutResult layout = ComposeSugiyamaLayoutEngine.Arrange(
            graph,
            graph.OrderedNodeIds.Select(id => new ComposeSugiyamaNode(id, 80, 50)),
            depthOrigin: 0,
            breadthOrigin: 0,
            depthGap: 50,
            siblingGap: 20);

        Equal(1, layout.DummyVertexCount, "one skipped layer requires one dummy");
        Equal(0, layout.LayerByNode["a"], "long edge source layer");
        Equal(2, layout.LayerByNode["c"], "long edge target layer");
        Near(0, layout.BreadthStart, 0.001, "dummy does not shift visible origin");
    }

    static void CheckSugiyamaCycleNormalization()
    {
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            new[] { "a", "b", "c", "tail" },
            new[]
            {
            new ComposeLayoutEdge("a", "b"),
            new ComposeLayoutEdge("b", "c"),
            new ComposeLayoutEdge("c", "a"),
            new ComposeLayoutEdge("c", "tail"),
            new ComposeLayoutEdge("tail", "tail")
            });
        ComposeSugiyamaLayoutResult layout = ComposeSugiyamaLayoutEngine.Arrange(
            graph,
            graph.OrderedNodeIds.Select(id => new ComposeSugiyamaNode(id, 80, 50)),
            depthOrigin: 0,
            breadthOrigin: 0,
            depthGap: 50,
            siblingGap: 20);

        Equal(ComposeLayoutGraphKind.Cyclic, graph.Kind, "cycle input kind");
        Equal(1, layout.ReversedEdges.Count, "one feedback edge reversed for layout");
        Equal(
            new ComposeLayoutEdge("c", "a"),
            layout.ReversedEdges[0],
            "stable SCC order chooses c to a");
        Equal(1, layout.SuppressedSelfLoops.Count, "self-loop excluded from layering");
        True(layout.LayerByNode["a"] < layout.LayerByNode["b"], "a before b after normalization");
        True(layout.LayerByNode["b"] < layout.LayerByNode["c"], "b before c after normalization");
        True(layout.LayerByNode["c"] < layout.LayerByNode["tail"], "cycle component before tail");
    }

    static void CheckSugiyamaDenseDagStability()
    {
        string[] nodeIds = Enumerable.Range(0, 12).Select(index => $"n{index}").ToArray();
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            nodeIds,
            new[]
            {
            new ComposeLayoutEdge("n0", "n3"),
            new ComposeLayoutEdge("n0", "n4"),
            new ComposeLayoutEdge("n1", "n4"),
            new ComposeLayoutEdge("n1", "n5"),
            new ComposeLayoutEdge("n2", "n3"),
            new ComposeLayoutEdge("n2", "n5"),
            new ComposeLayoutEdge("n3", "n6"),
            new ComposeLayoutEdge("n4", "n6"),
            new ComposeLayoutEdge("n4", "n7"),
            new ComposeLayoutEdge("n5", "n7"),
            new ComposeLayoutEdge("n0", "n8"),
            new ComposeLayoutEdge("n6", "n9"),
            new ComposeLayoutEdge("n7", "n9"),
            new ComposeLayoutEdge("n3", "n10"),
            new ComposeLayoutEdge("n8", "n10"),
            new ComposeLayoutEdge("n9", "n11"),
            new ComposeLayoutEdge("n10", "n11")
            });
        var sizes = nodeIds.ToDictionary(
            id => id,
            id =>
            {
                int index = int.Parse(id[1..]);
                return new ComposeSugiyamaNode(
                    id,
                    60 + ((index % 3) * 20),
                    40 + ((index % 4) * 15));
            });
        const double siblingGap = 18;
        ComposeSugiyamaLayoutResult layout = ComposeSugiyamaLayoutEngine.Arrange(
            graph,
            sizes.Values,
            depthOrigin: 25,
            breadthOrigin: 35,
            depthGap: 45,
            siblingGap);

        Equal(nodeIds.Length, layout.Positions.Count, "dense DAG position count");
        True(
            layout.FinalCrossingCount <= layout.InitialCrossingCount,
            "ordering sweeps never worsen the saved layout");
        foreach (ComposeSugiyamaPosition position in layout.Positions.Values)
        {
            True(double.IsFinite(position.Depth), "finite dense DAG depth");
            True(double.IsFinite(position.Breadth), "finite dense DAG breadth");
        }

        foreach (IReadOnlyList<string> layer in layout.OrderedLayers)
        {
            for (int index = 1; index < layer.Count; index++)
            {
                string previousId = layer[index - 1];
                string currentId = layer[index];
                double previousEnd =
                    layout.Positions[previousId].Breadth +
                    sizes[previousId].BreadthSize;
                double currentStart = layout.Positions[currentId].Breadth;
                True(
                    currentStart - previousEnd >= siblingGap - 0.001,
                    $"{previousId} and {currentId} dense DAG spacing");
            }
        }
    }

    static void CheckVolumeReverseStaircaseAndRankGap()
    {
        string[] services = { "root", "top", "middle", "bottom", "target" };
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["root"] = 0,
            ["top"] = 1,
            ["middle"] = 1,
            ["bottom"] = 1,
            ["target"] = 2
        };
        var serviceBreadths = services.ToDictionary(id => id, _ => 50d);
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            ranks,
            serviceBreadths,
            new[]
            {
            new ComposeVolumeLayoutItem("vTop", 80, 40, new[] { "top" }),
            new ComposeVolumeLayoutItem("vMiddle", 80, 40, new[] { "middle" }),
            new ComposeVolumeLayoutItem("vBottom", 80, 40, new[] { "bottom" })
            },
            volumeBreadthGap: 10);
        ComposeVolumeLayoutResult layout = ComposeVolumeLayoutEngine.Arrange(
            plan,
            new[]
            {
            new ComposeVolumeServiceSlot("root", 0, 0, 100, 50, 50),
            new ComposeVolumeServiceSlot("top", 1, 120, 0, 50, 50),
            new ComposeVolumeServiceSlot("middle", 1, 120, 100, 50, 50),
            new ComposeVolumeServiceSlot("bottom", 1, 120, 200, 50, 50),
            new ComposeVolumeServiceSlot("target", 2, 260, 100, 50, 50)
            },
            depthOrigin: 0,
            breadthOrigin: 0,
            volumeLeadGap: 20,
            volumeTrailGap: 20,
            volumeStairStep: 30,
            orphanGap: 80);

        True(
            layout.VolumePositions["vTop"].Depth > layout.VolumePositions["vMiddle"].Depth,
            "top owner volume must use a farther depth lane");
        True(
            layout.VolumePositions["vMiddle"].Depth > layout.VolumePositions["vBottom"].Depth,
            "bottom owner volume must use the nearest depth lane");
        True(
            layout.VolumePositions["vTop"].Breadth < layout.VolumePositions["vMiddle"].Breadth &&
            layout.VolumePositions["vMiddle"].Breadth < layout.VolumePositions["vBottom"].Breadth,
            "volume breadth follows owner top-to-bottom order");
        Near(180, layout.RequiredDepthGapByRank[1], 0.001, "three-volume required rank gap");
        Near(90, layout.DepthShiftByRank[2], 0.001, "next rank dynamic shift");
        Near(350, layout.ServicePositions["target"].Depth, 0.001, "shifted next rank position");
        Equal(2, layout.LaneByVolume["vTop"], "top volume farthest lane");
        Equal(0, layout.LaneByVolume["vBottom"], "bottom volume nearest lane");
    }

    static void CheckVolumeBranchPushesLowerSibling()
    {
        string[] services = { "root", "upper", "lower" };
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            services,
            new[]
            {
            new ComposeLayoutEdge("root", "upper"),
            new ComposeLayoutEdge("root", "lower")
            });
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            graph.RankByNode,
            services.ToDictionary(id => id, _ => 50d),
            new[] { new ComposeVolumeLayoutItem("upperVolume", 80, 40, new[] { "upper" }) },
            volumeBreadthGap: 10);
        ComposeTidyTreeLayoutResult tree = ComposeTidyTreeLayoutEngine.Arrange(
            graph,
            services.Select(id => new ComposeTidyTreeNode(
                id, 80, plan.ReservedBreadthByService[id])),
            0, 0, 70, 30, 100);
        ComposeVolumeLayoutResult layout = ComposeVolumeLayoutEngine.Arrange(
            plan,
            services.Select(id => new ComposeVolumeServiceSlot(
                id,
                graph.RankByNode[id],
                tree.Positions[id].Depth,
                tree.Positions[id].Breadth,
                80,
                50)),
            0, 0, 20, 20, 30, 80);

        ComposeVolumeAxisPosition volume = layout.VolumePositions["upperVolume"];
        double containerEnd = services.Max(id => tree.Positions[id].Breadth + 50);
        True(volume.Breadth >= containerEnd + 10 - 0.001,
            "volume shelf starts below every container in the component");
        True(tree.Positions["lower"].Breadth + 50 <= volume.Breadth - 10 + 0.001,
            "lower sibling remains above the volume shelf");
    }

    static void CheckVolumeInsideSharedNetwork()
    {
        string[] services = { "parent", "child" };
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["parent"] = 0,
            ["child"] = 1
        };
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            ranks,
            services.ToDictionary(id => id, _ => 50d),
            new[] { new ComposeVolumeLayoutItem("volume", 70, 40, new[] { "parent" }) },
            10);
        var slots = new[]
        {
        new ComposeVolumeServiceSlot("parent", 0, 0, 0, 50, 50),
        new ComposeVolumeServiceSlot("child", 1, 220, 0, 50, 50)
    };
        ComposeVolumeLayoutResult initial = ComposeVolumeLayoutEngine.Arrange(
            plan, slots, 0, 0, 20, 20, 30, 80);
        ComposeVolumeAxisPosition before = initial.VolumePositions["volume"];
        ComposeVolumeLayoutResult resolved = ComposeVolumeLayoutEngine.ResolveNetworkAwarePlacement(
            plan,
            slots,
            initial,
            new[]
            {
            new ComposeVolumeNetworkRegion(
                "network", -20, -20, 300, 150, new[] { "parent", "child" })
            },
            Descendants(("parent", new[] { "child" }), ("child", Array.Empty<string>())),
            60);

        Equal(before, resolved.VolumePositions["volume"],
            "volume remains in connector gap when shared network contains it");
    }

    static void CheckVolumeOutsideNetworkFallback()
    {
        string[] services = { "parent", "child", "leaf" };
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["parent"] = 0,
            ["child"] = 1,
            ["leaf"] = 2
        };
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            ranks,
            services.ToDictionary(id => id, _ => 50d),
            new[]
            {
            new ComposeVolumeLayoutItem("childVolume", 70, 40, new[] { "child" }),
            new ComposeVolumeLayoutItem("leafVolume", 70, 40, new[] { "leaf" })
            },
            10);
        var slots = new[]
        {
        new ComposeVolumeServiceSlot("parent", 0, 0, 0, 50, 50),
        new ComposeVolumeServiceSlot("child", 1, 180, 0, 50, 50),
        new ComposeVolumeServiceSlot("leaf", 2, 360, 0, 50, 50)
    };
        ComposeVolumeLayoutResult initial = ComposeVolumeLayoutEngine.Arrange(
            plan, slots, 0, 0, 20, 20, 30, 80);
        var region = new ComposeVolumeNetworkRegion(
            "network", -20, -20, 450, 100, services);
        ComposeVolumeServiceSlot[] shiftedSlots = slots
            .Select(service => service with
            {
                Depth = initial.ServicePositions[service.Id].Depth,
                Breadth = initial.ServicePositions[service.Id].Breadth
            })
            .ToArray();
        ComposeVolumeLayoutResult resolved = ComposeVolumeLayoutEngine.ResolveNetworkAwarePlacement(
            plan,
            shiftedSlots,
            initial,
            new[] { region },
            Descendants(
                ("parent", new[] { "child", "leaf" }),
                ("child", new[] { "leaf" }),
                ("leaf", Array.Empty<string>())),
            60);

        Near(resolved.VolumePositions["leafVolume"].Breadth,
             resolved.VolumePositions["childVolume"].Breadth,
             0.001,
             "one-volume rank groups start side by side instead of forming two floors");
        True(resolved.VolumePositions["leafVolume"].Depth >
             resolved.VolumePositions["childVolume"].Depth,
            "deeper-rank volume group occupies the right side of the shared corridor");
    }

    static void CheckVolumeRequiresActualDescendant()
    {
        string[] services = { "owner", "unrelated" };
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["owner"] = 0,
            ["unrelated"] = 1
        };
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            ranks,
            services.ToDictionary(id => id, _ => 50d),
            new[] { new ComposeVolumeLayoutItem("volume", 70, 40, new[] { "owner" }) },
            10);
        var slots = new[]
        {
        new ComposeVolumeServiceSlot("owner", 0, 0, 0, 50, 50),
        new ComposeVolumeServiceSlot("unrelated", 1, 220, 0, 50, 50)
    };
        ComposeVolumeLayoutResult initial = ComposeVolumeLayoutEngine.Arrange(
            plan, slots, 0, 0, 20, 20, 30, 80);
        var region = new ComposeVolumeNetworkRegion(
            "network", -20, -20, 300, 150, services);
        ComposeVolumeServiceSlot[] shiftedSlots = slots
            .Select(service => service with
            {
                Depth = initial.ServicePositions[service.Id].Depth,
                Breadth = initial.ServicePositions[service.Id].Breadth
            })
            .ToArray();
        ComposeVolumeLayoutResult resolved = ComposeVolumeLayoutEngine.ResolveNetworkAwarePlacement(
            plan,
            shiftedSlots,
            initial,
            new[] { region },
            Descendants(("owner", Array.Empty<string>()), ("unrelated", Array.Empty<string>())),
            60);

        True(resolved.ExternalVolumeIds.Contains("volume"),
            "same-network later rank must not be mistaken for a real child");
        True(resolved.VolumePositions["volume"].Breadth >= region.BreadthEnd + 60 - 0.001,
            "volume without a real descendant moves below the network");
    }

    static void CheckVolumeOverlappingNetworkIntersection()
    {
        string[] services = { "parent", "child" };
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["parent"] = 0,
            ["child"] = 1
        };
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            ranks,
            services.ToDictionary(id => id, _ => 50d),
            new[] { new ComposeVolumeLayoutItem("volume", 70, 40, new[] { "parent" }) },
            10);
        var slots = new[]
        {
        new ComposeVolumeServiceSlot("parent", 0, 0, 0, 50, 50),
        new ComposeVolumeServiceSlot("child", 1, 220, 0, 50, 50)
    };
        ComposeVolumeLayoutResult initial = ComposeVolumeLayoutEngine.Arrange(
            plan, slots, 0, 0, 20, 20, 30, 80);
        var regions = new[]
        {
        new ComposeVolumeNetworkRegion("networkA", -20, -20, 300, 150, services),
        new ComposeVolumeNetworkRegion("networkB", -20, 80, 300, 150, services)
    };
        ComposeVolumeLayoutResult resolved = ComposeVolumeLayoutEngine.ResolveNetworkAwarePlacement(
            plan,
            slots,
            initial,
            regions,
            Descendants(("parent", new[] { "child" }), ("child", Array.Empty<string>())),
            60);

        True(resolved.ExternalVolumeIds.Contains("volume"),
            "candidate that fits only one overlapping network must use the outside shelf");
    }

    static void CheckSharedVolumeValidatesEveryOwnerBranch()
    {
        string[] services = { "left", "right", "leftChild" };
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["left"] = 0,
            ["right"] = 0,
            ["leftChild"] = 1
        };
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            ranks,
            services.ToDictionary(id => id, _ => 50d),
            new[] { new ComposeVolumeLayoutItem("shared", 70, 40, new[] { "left", "right" }) },
            10);
        var slots = new[]
        {
        new ComposeVolumeServiceSlot("left", 0, 0, 0, 50, 50),
        new ComposeVolumeServiceSlot("right", 0, 0, 100, 50, 50),
        new ComposeVolumeServiceSlot("leftChild", 1, 220, 0, 50, 50)
    };
        ComposeVolumeLayoutResult initial = ComposeVolumeLayoutEngine.Arrange(
            plan, slots, 0, 0, 20, 20, 30, 80);
        var region = new ComposeVolumeNetworkRegion(
            "network", -20, -20, 300, 260, services);
        ComposeVolumeLayoutResult resolved = ComposeVolumeLayoutEngine.ResolveNetworkAwarePlacement(
            plan,
            slots,
            initial,
            new[] { region },
            Descendants(
                ("left", new[] { "leftChild" }),
                ("right", Array.Empty<string>()),
                ("leftChild", Array.Empty<string>())),
            60);

        True(resolved.ExternalVolumeIds.Contains("shared"),
            "shared volume cannot stay inline when one owner branch has no descendant corridor");
    }

    static void CheckExternalShelvesRemainComponentLocal()
    {
        string[] services = { "ownerA", "ownerB" };
        var ranks = services.ToDictionary(id => id, _ => 0, StringComparer.OrdinalIgnoreCase);
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            ranks,
            services.ToDictionary(id => id, _ => 50d),
            new[]
            {
            new ComposeVolumeLayoutItem("volumeA", 70, 40, new[] { "ownerA" }),
            new ComposeVolumeLayoutItem("volumeB", 70, 40, new[] { "ownerB" })
            },
            10);
        var slots = new[]
        {
        new ComposeVolumeServiceSlot("ownerA", 0, 0, 0, 50, 50),
        new ComposeVolumeServiceSlot("ownerB", 0, 600, 500, 50, 50)
    };
        ComposeVolumeLayoutResult initial = ComposeVolumeLayoutEngine.Arrange(
            plan, slots, 0, 0, 20, 20, 30, 80);
        var regionA = new ComposeVolumeNetworkRegion(
            "networkA", -20, -20, 300, 120, new[] { "ownerA" });
        var regionB = new ComposeVolumeNetworkRegion(
            "networkB", 580, 480, 300, 120, new[] { "ownerB" });
        ComposeVolumeLayoutResult resolved = ComposeVolumeLayoutEngine.ResolveNetworkAwarePlacement(
            plan,
            slots,
            initial,
            new[] { regionA, regionB },
            Descendants(("ownerA", Array.Empty<string>()), ("ownerB", Array.Empty<string>())),
            60);

        True(resolved.VolumePositions["volumeA"].Breadth < regionB.Breadth,
            "disjoint network A must not be pushed below distant network B");
        True(resolved.VolumePositions["volumeB"].Breadth >= regionB.BreadthEnd + 60 - 0.001,
            "network B uses its own outside shelf");
    }

    static void CheckExternalVolumeShelvesPreserveOwnerRanks()
    {
        string[] services = { "root", "middleTop", "middleBottom", "leaf" };
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["root"] = 0,
            ["middleTop"] = 1,
            ["middleBottom"] = 1,
            ["leaf"] = 2
        };
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            ranks,
            services.ToDictionary(id => id, _ => 50d),
            new[]
            {
            new ComposeVolumeLayoutItem("middleTopVolume", 80, 40, new[] { "middleTop" }),
            new ComposeVolumeLayoutItem("middleBottomVolume", 80, 40, new[] { "middleBottom" }),
            new ComposeVolumeLayoutItem("leafVolume", 80, 40, new[] { "leaf" })
            },
            10);
        var slots = new[]
        {
        new ComposeVolumeServiceSlot("root", 0, 0, 0, 50, 50),
        new ComposeVolumeServiceSlot("middleTop", 1, 180, 0, 50, 50),
        new ComposeVolumeServiceSlot("middleBottom", 1, 180, 100, 50, 50),
        new ComposeVolumeServiceSlot("leaf", 2, 360, 0, 50, 50)
    };
        ComposeVolumeLayoutResult initial = ComposeVolumeLayoutEngine.Arrange(
            plan, slots, 0, 0, 20, 20, 30, 80);
        var region = new ComposeVolumeNetworkRegion(
            "network", -20, -20, 450, 100, services);
        ComposeVolumeServiceSlot[] shiftedSlots = slots
            .Select(service => service with
            {
                Depth = initial.ServicePositions[service.Id].Depth,
                Breadth = initial.ServicePositions[service.Id].Breadth
            })
            .ToArray();
        ComposeVolumeLayoutResult resolved = ComposeVolumeLayoutEngine.ResolveNetworkAwarePlacement(
            plan,
            shiftedSlots,
            initial,
            new[] { region },
            Descendants(
                ("root", new[] { "middleTop", "middleBottom", "leaf" }),
                ("middleTop", new[] { "leaf" }),
                ("middleBottom", new[] { "leaf" }),
                ("leaf", Array.Empty<string>())),
            60);

        ComposeVolumeAxisPosition middleTop = resolved.VolumePositions["middleTopVolume"];
        ComposeVolumeAxisPosition middleBottom = resolved.VolumePositions["middleBottomVolume"];
        ComposeVolumeAxisPosition leafVolume = resolved.VolumePositions["leafVolume"];
        True(middleTop.Depth > middleBottom.Depth,
            "upper volume uses the farthest-right lane inside its owner rank");
        True(middleTop.Breadth < middleBottom.Breadth,
            "same-rank volumes keep owner order from top to bottom");
        True(leafVolume.Depth > middleTop.Depth,
            "leaf-rank volume takes the first farthest-right lane instead of creating another rank");
        Near(leafVolume.Breadth, middleTop.Breadth, 0.001,
            "leaf one-level group starts beside the middle two-level group");

        ComposeVolumeServiceSlot[] finalSlots = shiftedSlots
            .Select(service => service with
            {
                Depth = resolved.ServicePositions[service.Id].Depth,
                Breadth = resolved.ServicePositions[service.Id].Breadth
            })
            .ToArray();
        foreach ((string volumeId, ComposeVolumeAxisPosition volumePosition) in
                 resolved.VolumePositions.Where(pair => resolved.ExternalVolumeIds.Contains(pair.Key)))
        {
            ComposeVolumeLayoutItem volume = plan.Volumes[volumeId];
            foreach (ComposeVolumeServiceSlot service in finalSlots)
            {
                bool overlapsContainerDepth =
                    volumePosition.Depth < service.Depth + service.DepthSize &&
                    volumePosition.Depth + volume.DepthSize > service.Depth;
                True(!overlapsContainerDepth,
                    $"external volume '{volumeId}' must not sit directly below container '{service.Id}'");
            }
        }
    }

    static void CheckExternalVolumeExcludedFromInlineEnvelope()
    {
        string[] services = { "owner", "next" };
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["owner"] = 0,
            ["next"] = 1
        };
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            ranks,
            services.ToDictionary(id => id, _ => 50d),
            new[]
            {
            new ComposeVolumeLayoutItem("inline", 70, 40, new[] { "owner" }),
            new ComposeVolumeLayoutItem("external", 70, 40, new[] { "owner" })
            },
            10,
            new HashSet<string>(new[] { "inline" }, StringComparer.OrdinalIgnoreCase));
        ComposeVolumeLayoutResult layout = ComposeVolumeLayoutEngine.Arrange(
            plan,
            new[]
            {
            new ComposeVolumeServiceSlot("owner", 0, 0, 0, 50, 50),
            new ComposeVolumeServiceSlot("next", 1, 220, 0, 50, 50)
            },
            0, 0, 20, 20, 30, 80);

        Near(160, plan.ReservedBreadthByService["owner"], 0.001,
            "only the inline volume is included in the component shelf envelope");
        True(layout.VolumePositions.ContainsKey("inline"), "inline volume receives a corridor position");
        True(!layout.VolumePositions.ContainsKey("external"),
            "external volume does not leave an empty corridor during the second pass");
    }

    static void CheckVolumeEnvelopeSeparatesTreeBranches()
    {
        string[] services = { "root", "upper", "lower" };
        ComposeGraphTopology graph = ComposeGraphTopologyAnalyzer.Analyze(
            services,
            new[]
            {
            new ComposeLayoutEdge("root", "upper"),
            new ComposeLayoutEdge("root", "lower")
            });
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            graph.RankByNode,
            services.ToDictionary(id => id, _ => 50d),
            new[]
            {
            new ComposeVolumeLayoutItem("v1", 80, 40, new[] { "upper" }),
            new ComposeVolumeLayoutItem("v2", 80, 50, new[] { "upper" }),
            new ComposeVolumeLayoutItem("v3", 80, 60, new[] { "upper" })
            },
            volumeBreadthGap: 10);
        ComposeTidyTreeLayoutResult tree = ComposeTidyTreeLayoutEngine.Arrange(
            graph,
            services.Select(id => new ComposeTidyTreeNode(
                id,
                DepthSize: 80,
                BreadthSize: plan.ReservedBreadthByService[id])),
            depthOrigin: 0,
            breadthOrigin: 0,
            depthGap: 70,
            siblingGap: 30,
            forestGap: 100);

        Near(350, plan.ReservedBreadthByService["root"], 0.001,
            "component anchor reserves containers plus the complete volume shelf");
        Near(50, plan.ReservedBreadthByService["upper"], 0.001,
            "volume owner keeps its real node height instead of creating an inline gap");
        True(tree.Positions["lower"].Breadth >= tree.Positions["upper"].Breadth + 80 - 0.001,
            "container siblings retain ordinary spacing before the separate volume shelf");
    }

    static void CheckVolumeLargeCountSpacing()
    {
        string[] services = { "owner", "next" };
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["owner"] = 0,
            ["next"] = 1
        };
        ComposeVolumeLayoutItem[] volumes = Enumerable.Range(0, 20)
            .Select(index => new ComposeVolumeLayoutItem(
                $"v{index}",
                80,
                35,
                new[] { "owner" }))
            .ToArray();
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            ranks,
            services.ToDictionary(id => id, _ => 50d),
            volumes,
            volumeBreadthGap: 8);
        ComposeVolumeLayoutResult layout = ComposeVolumeLayoutEngine.Arrange(
            plan,
            new[]
            {
            new ComposeVolumeServiceSlot("owner", 0, 0, 0, 50, 50),
            new ComposeVolumeServiceSlot("next", 1, 150, 0, 50, 50)
            },
            depthOrigin: 0,
            breadthOrigin: 0,
            volumeLeadGap: 20,
            volumeTrailGap: 20,
            volumeStairStep: 30,
            orphanGap: 80);

        Near(690, layout.RequiredDepthGapByRank[0], 0.001, "twenty-volume dynamic gap");
        Near(590, layout.DepthShiftByRank[1], 0.001, "twenty-volume next-rank shift");
        Equal(19, layout.LaneByVolume["v0"], "first volume farthest lane");
        Equal(0, layout.LaneByVolume["v19"], "last volume nearest lane");
    }

    static void CheckSharedAndOrphanVolumePlanning()
    {
        string[] services = { "early", "deep" };
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["early"] = 0,
            ["deep"] = 1
        };
        ComposeVolumeLayoutPlan plan = ComposeVolumeLayoutEngine.CreatePlan(
            services,
            ranks,
            services.ToDictionary(id => id, _ => 50d),
            new[]
            {
            new ComposeVolumeLayoutItem("shared", 70, 45, new[] { "early", "deep" }),
            new ComposeVolumeLayoutItem("orphan", 70, 45, Array.Empty<string>())
            },
            volumeBreadthGap: 10);
        ComposeVolumeLayoutResult layout = ComposeVolumeLayoutEngine.Arrange(
            plan,
            new[]
            {
            new ComposeVolumeServiceSlot("early", 0, 0, 0, 50, 50),
            new ComposeVolumeServiceSlot("deep", 1, 140, 0, 50, 50)
            },
            depthOrigin: 5,
            breadthOrigin: 10,
            volumeLeadGap: 20,
            volumeTrailGap: 20,
            volumeStairStep: 30,
            orphanGap: 80);

        Equal("deep", plan.PrimaryOwnerByVolume["shared"], "shared volume uses deepest owner");
        Equal(1, plan.AnchorRankByVolume["shared"], "shared volume anchor rank");
        True(plan.OrphanVolumeIds.Contains("orphan"), "orphan volume classification");
        Near(5, layout.VolumePositions["orphan"].Depth, 0.001, "orphan depth origin");
        True(
            layout.VolumePositions["orphan"].Breadth >
            layout.VolumePositions["shared"].Breadth + 45,
            "orphan placed after owned volume envelope");
    }

    static void CheckNetworkIdenticalMembershipRings()
    {
        ComposeNetworkLayoutResult layout = ComposeNetworkLayoutEngine.Arrange(
            new[]
            {
            new ComposeNetworkLayoutNode("node", 100, 100, 80, 50)
            },
            new[]
            {
            new ComposeNetworkLayoutGroup("outer", "Zulu", new[] { "node" }),
            new ComposeNetworkLayoutGroup("inner", "Alpha", new[] { "node" })
            },
            sidePadding: 28,
            topPadding: 48,
            bottomPadding: 28,
            coincidentRingGap: 28,
            headerHeight: 24,
            headerGap: 4,
            minimumWidth: 300,
            minimumHeight: 200);

        Equal(0, layout.RingIndexByNetwork["inner"], "alphabetically stable inner ring");
        Equal(1, layout.RingIndexByNetwork["outer"], "alphabetically stable outer ring");
        ComposeNetworkLayoutRect inner = layout.BoundsByNetwork["inner"];
        ComposeNetworkLayoutRect outer = layout.BoundsByNetwork["outer"];
        True(outer.Contains(inner), "outer identical-membership ring contains inner ring");
        Near(56, outer.Width - inner.Width, 0.001, "ring width expansion");
        Equal(
            ComposeNetworkRelationKind.Identical,
            layout.Relations.Single().Kind,
            "identical network relation");
    }

    static void CheckNetworkStrictContainment()
    {
        ComposeNetworkLayoutResult layout = ComposeNetworkLayoutEngine.Arrange(
            new[]
            {
            new ComposeNetworkLayoutNode("a", 0, 0, 80, 50),
            new ComposeNetworkLayoutNode("b", 260, 0, 80, 50)
            },
            new[]
            {
            new ComposeNetworkLayoutGroup("large", "Large", new[] { "a", "b" }),
            new ComposeNetworkLayoutGroup("small", "Small", new[] { "a" })
            },
            sidePadding: 28,
            topPadding: 48,
            bottomPadding: 28,
            coincidentRingGap: 28,
            headerHeight: 24,
            headerGap: 4,
            minimumWidth: 300,
            minimumHeight: 200);

        ComposeNetworkLayoutRect large = layout.BoundsByNetwork["large"];
        ComposeNetworkLayoutRect small = layout.BoundsByNetwork["small"];
        True(large.Contains(small), "superset network contains subset group bounds");
        Equal("large", layout.ParentNetworkByNetwork["small"], "subset parent network");
        Equal(
            ComposeNetworkRelationKind.LeftContainsRight,
            layout.Relations.Single().Kind,
            "strict containment relation");
    }

    static void CheckNetworkPartialOverlapAndHeaders()
    {
        var nodes = new[]
        {
        new ComposeNetworkLayoutNode("left", 0, 100, 80, 50),
        new ComposeNetworkLayoutNode("shared", 150, 100, 80, 50),
        new ComposeNetworkLayoutNode("right", 300, 100, 80, 50)
    };
        ComposeNetworkLayoutResult layout = ComposeNetworkLayoutEngine.Arrange(
            nodes,
            new[]
            {
            new ComposeNetworkLayoutGroup("networkA", "Network A", new[] { "left", "shared" }),
            new ComposeNetworkLayoutGroup("networkB", "Network B", new[] { "shared", "right" })
            },
            sidePadding: 28,
            topPadding: 48,
            bottomPadding: 28,
            coincidentRingGap: 28,
            headerHeight: 24,
            headerGap: 4,
            minimumWidth: 300,
            minimumHeight: 200);

        ComposeNetworkLayoutRect leftBounds = layout.BoundsByNetwork["networkA"];
        ComposeNetworkLayoutRect rightBounds = layout.BoundsByNetwork["networkB"];
        ComposeNetworkLayoutRect sharedNode = new(150, 100, 80, 50);
        True(leftBounds.Contains(sharedNode), "left partial network contains shared node");
        True(rightBounds.Contains(sharedNode), "right partial network contains shared node");
        True(leftBounds.Intersects(rightBounds), "partial networks retain a real overlap");
        Equal(
            ComposeNetworkRelationKind.PartialOverlap,
            layout.Relations.Single().Kind,
            "partial overlap relation");
        Equal("shared", layout.Relations.Single().SharedMemberIds.Single(), "shared member");

        ComposeNetworkLayoutRect leftHeader = new(
            leftBounds.X,
            leftBounds.Y,
            Math.Min(leftBounds.Width, 190),
            24);
        ComposeNetworkLayoutRect rightHeader = new(
            rightBounds.X,
            rightBounds.Y,
            Math.Min(rightBounds.Width, 190),
            24);
        True(!leftHeader.Intersects(rightHeader), "overlapping network headers use separate lanes");
    }

    static void CheckNetworkDisjointBounds()
    {
        ComposeNetworkLayoutResult layout = ComposeNetworkLayoutEngine.Arrange(
            new[]
            {
            new ComposeNetworkLayoutNode("left", 0, 0, 80, 50),
            new ComposeNetworkLayoutNode("right", 1000, 0, 80, 50)
            },
            new[]
            {
            new ComposeNetworkLayoutGroup("networkA", "Network A", new[] { "left" }),
            new ComposeNetworkLayoutGroup("networkB", "Network B", new[] { "right" })
            },
            sidePadding: 28,
            topPadding: 48,
            bottomPadding: 28,
            coincidentRingGap: 28,
            headerHeight: 24,
            headerGap: 4,
            minimumWidth: 300,
            minimumHeight: 200);

        True(
            !layout.BoundsByNetwork["networkA"].Intersects(layout.BoundsByNetwork["networkB"]),
            "separated memberships remain visually disjoint when topology permits");
        Equal(
            ComposeNetworkRelationKind.Disjoint,
            layout.Relations.Single().Kind,
            "disjoint network relation");
    }

    static void CheckSwarmConnectionPolicy()
    {
        SwarmConnectionDecision dependency = SwarmConnectionPolicy.Resolve(
            RuntimeResourceKind.SwarmService,
            RuntimeResourceKind.SwarmService);
        True(dependency.IsAllowed, "service dependency is allowed");
        Equal(RelationType.Dependency, dependency.RelationType, "service dependency relation");

        SwarmConnectionDecision volume = SwarmConnectionPolicy.Resolve(
            RuntimeResourceKind.SwarmVolume,
            RuntimeResourceKind.SwarmService);
        True(volume.IsAllowed, "service volume is allowed");
        Equal(RelationType.VolumeMount, volume.RelationType, "service volume relation");
        True(volume.ReverseDirection, "volume to service normalizes to service to volume");

        SwarmConnectionDecision published = SwarmConnectionPolicy.Resolve(
            RuntimeResourceKind.SwarmService,
            RuntimeResourceKind.SwarmExternalTraffic);
        True(published.IsAllowed, "external traffic service is allowed");
        Equal(RelationType.SwarmPublishedPort, published.RelationType, "published port relation");
        True(published.ReverseDirection, "service to external normalizes to external to service");

        Equal(
            RelationType.SwarmSecretReference,
            SwarmConnectionPolicy.Resolve(RuntimeResourceKind.SwarmService, RuntimeResourceKind.SwarmSecret).RelationType,
            "secret relation");
        Equal(
            RelationType.SwarmConfigReference,
            SwarmConnectionPolicy.Resolve(RuntimeResourceKind.SwarmService, RuntimeResourceKind.SwarmConfig).RelationType,
            "config relation");

        True(
            !SwarmConnectionPolicy.Resolve(RuntimeResourceKind.SwarmService, RuntimeResourceKind.SwarmOverlayNetwork).IsAllowed,
            "overlay network uses containment instead of a connector");
        True(
            !SwarmConnectionPolicy.Resolve(RuntimeResourceKind.SwarmVolume, RuntimeResourceKind.SwarmExternalTraffic).IsAllowed,
            "volume external traffic is rejected");
        True(
            SwarmConnectionPolicy.Resolve(RuntimeResourceKind.SwarmVisualGroup, RuntimeResourceKind.SwarmService).IsAllowed,
            "visual group retains connector behavior");
    }

    static void CheckSwarmTaskTopology()
    {
        var nodes = new[]
        {
        new DockerSwarmNode { Id = "node-b", Hostname = "worker-b", Role = "worker", Status = "ready" },
        new DockerSwarmNode { Id = "node-a", Hostname = "manager-a", Role = "manager", Status = "ready" }
    };
        var tasks = new[]
        {
        new DockerSwarmTask { Id = "task-2", Slot = 2, NodeId = "node-b", NodeName = "worker-b", CurrentState = "running" },
        new DockerSwarmTask { Id = "task-1", Slot = 1, NodeId = "node-a", NodeName = "manager-a", CurrentState = "running" },
        new DockerSwarmTask { Id = "task-3", Slot = 3, CurrentState = "pending" }
    };

        IReadOnlyList<SwarmTaskPlacement> placements = SwarmTaskTopology.Build(tasks, nodes);
        Equal(3, placements.Count, "task placements include assigned and pending groups");
        Equal("manager-a", placements[0].NodeName, "assigned nodes are ordered by hostname");
        Equal("worker-b", placements[1].NodeName, "second assigned node");
        True(!placements[2].IsAssigned, "unassigned tasks are kept in a pending group");
        Equal((ulong)3, placements[2].Tasks.Single().Slot, "pending task remains visible");
        Equal("manager · ready", placements[0].NodeSummary, "node role and state are projected");
    }

    static void CheckSwarmClusterState()
    {
        SwarmClusterState inactive = SwarmClusterState.Create("inactive", false);
        Equal(SwarmMembershipState.Inactive, inactive.Membership, "inactive engine");
        True(!inactive.IsActive, "inactive engine is not active");

        SwarmClusterState manager = SwarmClusterState.Create("ACTIVE", true, "manager-id", "10.0.0.10");
        Equal(SwarmMembershipState.Manager, manager.Membership, "active control node is manager");
        True(manager.IsManager, "manager flag");

        SwarmClusterState worker = SwarmClusterState.Create(
            "active",
            false,
            "worker-id",
            "10.0.0.11",
            remoteManagers: new[] { new SwarmManagerEndpoint("manager-id", "10.0.0.10:2377") });
        Equal(SwarmMembershipState.Worker, worker.Membership, "active non-control node is worker");
        True(worker.IsWorker, "worker flag");
        Equal("10.0.0.10:2377", worker.RemoteManagers.Single().Address, "worker manager address");

        Equal(SwarmMembershipState.Pending, SwarmClusterState.Create("pending", false).Membership, "pending engine");
        Equal(SwarmMembershipState.Locked, SwarmClusterState.Create("locked", false).Membership, "locked engine");
        Equal(SwarmMembershipState.Error, SwarmClusterState.Create("error", false, errorMessage: "boom").Membership, "error engine");
        Equal(SwarmMembershipState.Error, SwarmClusterState.Create(null, false, errorMessage: "missing state").Membership, "missing state with error");
        Equal(SwarmMembershipState.Unknown, SwarmClusterState.Create(null, false).Membership, "missing state without error");
    }

    static void CheckSwarmLifecycleOptions()
    {
        var initialize = new SwarmInitializeOptions
        {
            ListenAddress = "0.0.0.0:2377",
            AdvertiseAddress = "10.0.0.10:2377",
            DataPathPort = 4789,
            Availability = "ACTIVE"
        };
        initialize.Validate();

        Throws<ArgumentException>(
            () => new SwarmInitializeOptions { AdvertiseAddress = "" }.Validate(),
            "initialize requires advertise address");
        Throws<ArgumentOutOfRangeException>(
            () => new SwarmInitializeOptions { AdvertiseAddress = "10.0.0.10", DataPathPort = 80 }.Validate(),
            "initialize rejects reserved data path port");

        var join = new SwarmJoinOptions
        {
            RemoteManagerAddresses = new[] { " 10.0.0.10:2377 ", "10.0.0.10:2377", "manager-b:2377" },
            JoinToken = "SWMTKN-test",
            Role = SwarmJoinRole.Worker
        };
        join.Validate();
        Equal(2, join.GetNormalizedManagerAddresses().Count, "join manager addresses normalize and deduplicate");
        True(!join.ToString().Contains("SWMTKN-test", StringComparison.Ordinal), "join request string redacts token");

        Throws<ArgumentException>(
            () => new SwarmJoinOptions { JoinToken = "token" }.Validate(),
            "join requires manager address");
        Throws<ArgumentException>(
            () => new SwarmJoinOptions { RemoteManagerAddresses = new[] { "manager:2377" } }.Validate(),
            "join requires token");

        var tokens = new SwarmJoinTokens("worker-secret", "manager-secret");
        Equal("worker-secret", tokens.GetToken(SwarmJoinRole.Worker), "worker token selection");
        Equal("manager-secret", tokens.GetToken(SwarmJoinRole.Manager), "manager token selection");
        True(!tokens.ToString().Contains("worker-secret", StringComparison.Ordinal), "token set string is redacted");
    }

    static void CheckSwarmResourceFilter()
    {
        True(
            SwarmResourceFilter.IsOverlayNetwork(new DockerNetworkGroup { Driver = "overlay", Scope = "local" }),
            "overlay driver is shown");
        True(
            SwarmResourceFilter.IsOverlayNetwork(new DockerNetworkGroup { Driver = "custom", Scope = "swarm" }),
            "swarm scope is shown");
        True(
            !SwarmResourceFilter.IsOverlayNetwork(new DockerNetworkGroup { Driver = "bridge", Scope = "local" }),
            "local bridge is hidden");
    }

    static void CheckSwarmSetupPresentation()
    {
        Equal(
            SwarmSetupPanelKind.Inactive,
            SwarmSetupPresentation.FromState(SwarmClusterState.Create("inactive", false)).Panel,
            "inactive setup panel");
        Equal(
            SwarmSetupPanelKind.Manager,
            SwarmSetupPresentation.FromState(SwarmClusterState.Create("active", true)).Panel,
            "manager setup panel");
        Equal(
            SwarmSetupPanelKind.Worker,
            SwarmSetupPresentation.FromState(SwarmClusterState.Create("active", false)).Panel,
            "worker setup panel");
        Equal(
            SwarmSetupPanelKind.Waiting,
            SwarmSetupPresentation.FromState(SwarmClusterState.Create("pending", false)).Panel,
            "pending setup panel");

        SwarmSetupPresentation error = SwarmSetupPresentation.FromState(
            SwarmClusterState.Create("error", false, errorMessage: "daemon error"));
        Equal(SwarmSetupPanelKind.Error, error.Panel, "error setup panel");
        Equal("daemon error", error.StatusDescription, "daemon error is preserved");
    }

    static void CheckSwarmAdvertiseAddressDiscovery()
    {
        True(
            SwarmAdvertiseAddressDiscovery.IsUsableIpv4(System.Net.IPAddress.Parse("192.168.10.20")),
            "private IPv4 is usable");
        True(
            SwarmAdvertiseAddressDiscovery.IsUsableIpv4(System.Net.IPAddress.Parse("8.8.8.8")),
            "non-loopback IPv4 remains manually usable");
        True(
            !SwarmAdvertiseAddressDiscovery.IsUsableIpv4(System.Net.IPAddress.Loopback),
            "loopback IPv4 is rejected");
        True(
            !SwarmAdvertiseAddressDiscovery.IsUsableIpv4(System.Net.IPAddress.Parse("169.254.10.20")),
            "link-local IPv4 is rejected");
        True(
            !SwarmAdvertiseAddressDiscovery.IsUsableIpv4(System.Net.IPAddress.IPv6Loopback),
            "IPv6 is not offered by the IPv4 candidate picker");

        var bridge = new NetworkResponse
        {
            IPAM = new IPAM
            {
                Config = new List<IPAMConfig>
                {
                    new() { Gateway = "172.17.0.1" },
                    new() { Gateway = "fe80::1" },
                    new() { Gateway = "172.17.0.1" }
                }
            }
        };
        IReadOnlyList<string> daemonAddresses =
            SwarmAdvertiseAddressDiscovery.GetDaemonBridgeIpv4Candidates(bridge);
        Equal(1, daemonAddresses.Count, "daemon bridge candidates are IPv4 and distinct");
        Equal("172.17.0.1", daemonAddresses[0], "daemon bridge gateway is selected");
    }

    static void CheckSwarmJoinCommand()
    {
        Equal(
            "manager.local:2377",
            SwarmJoinCommandBuilder.NormalizeManagerAddress("manager.local"),
            "default manager port");
        Equal(
            "10.0.0.10:2378",
            SwarmJoinCommandBuilder.NormalizeManagerAddress("10.0.0.10:2378"),
            "explicit manager port");
        Equal(
            "[2001:db8::1]:2377",
            SwarmJoinCommandBuilder.NormalizeManagerAddress("2001:db8::1"),
            "IPv6 manager normalization");
        Equal(
            "docker swarm join --token SWMTKN-1-test 10.0.0.10:2377",
            SwarmJoinCommandBuilder.Build("SWMTKN-1-test", "10.0.0.10"),
            "join command");
        Equal(
            "docker swarm join --token <hidden> 10.0.0.10:2377",
            SwarmJoinCommandBuilder.BuildRedacted("10.0.0.10"),
            "redacted join command");

        Throws<ArgumentException>(
            () => SwarmJoinCommandBuilder.Build("token;shutdown", "10.0.0.10"),
            "join token shell characters are rejected");
        Throws<ArgumentException>(
            () => SwarmJoinCommandBuilder.Build("token", "10.0.0.10;shutdown"),
            "manager shell characters are rejected");
        Throws<ArgumentException>(
            () => SwarmJoinCommandBuilder.NormalizeManagerAddress("manager:70000"),
            "invalid manager port");

        IReadOnlyList<string> sshArguments = SshTunnelManager.BuildOpenSshArguments(
            "10.0.0.12", 2222, "ubuntu", "id_ed25519", "/var/run/docker.sock", 23750);
        int destinationIndex = sshArguments.ToList().IndexOf("ubuntu@10.0.0.12");
        int portOptionIndex = sshArguments.ToList().IndexOf("-p");
        Equal(sshArguments.Count - 1, destinationIndex, "SSH destination must be the final argument");
        True(portOptionIndex >= 0 && portOptionIndex < destinationIndex, "SSH port option precedes destination");
        True(sshArguments.Contains("ExitOnForwardFailure=yes"), "SSH forward setup failure is fatal");
    }

    static void CheckSwarmTargetConnectionOptions()
    {
        var options = new SwarmTargetConnectionOptions
        {
            Host = "node-2.local",
            SshPort = 22,
            Username = "docker-user",
            SshKeyFilePath = "private-key.pem",
            RemoteDockerSocketPath = "/var/run/docker.sock",
            Role = SwarmJoinRole.Manager,
            AdvertiseAddress = "10.0.0.12"
        };
        options.Validate();
        Equal(
            "Manager target · node-2.local:22 · advertise 10.0.0.12",
            options.ToString(),
            "target summary excludes SSH key");
        True(!options.ToString().Contains("private-key", StringComparison.Ordinal), "SSH key is redacted from summary");

        Throws<ArgumentOutOfRangeException>(
            () => new SwarmTargetConnectionOptions
            {
                Host = "node",
                SshPort = 70000,
                Username = "user",
                SshKeyFilePath = "key",
                AdvertiseAddress = "10.0.0.2"
            }.Validate(),
            "invalid SSH port");
        Throws<ArgumentException>(
            () => new SwarmTargetConnectionOptions
            {
                Host = "node",
                Username = "user",
                SshKeyFilePath = "key"
            }.Validate(),
            "missing advertise address");
    }

    static void CheckSwarmJoinVerification()
    {
        True(
            SwarmJoinVerification.HasExpectedRole(
                SwarmClusterState.Create("active", false),
                SwarmJoinRole.Worker),
            "worker role confirmation");
        True(
            SwarmJoinVerification.HasExpectedRole(
                SwarmClusterState.Create("active", true),
                SwarmJoinRole.Manager),
            "manager role confirmation");
        True(
            !SwarmJoinVerification.HasExpectedRole(
                SwarmClusterState.Create("pending", false),
                SwarmJoinRole.Worker),
            "pending state is not complete");

        var nodes = new[]
        {
        new DockerSwarmNode { Id = "worker-id", Role = "worker", Address = "10.0.0.12", Hostname = "node-2" },
        new DockerSwarmNode { Id = "manager-id", Role = "manager", Address = "10.0.0.10", Hostname = "node-1" }
    };
        Equal(
            "worker-id",
            SwarmJoinVerification.FindJoinedNode(nodes, "worker-id", "10.0.0.99", SwarmJoinRole.Worker)?.Id,
            "joined node matches stable node id");
        Equal(
            "worker-id",
            SwarmJoinVerification.FindJoinedNode(nodes, string.Empty, "10.0.0.12:2377", SwarmJoinRole.Worker)?.Id,
            "joined node falls back to normalized address");
        True(
            SwarmJoinVerification.FindJoinedNode(nodes, "worker-id", "10.0.0.12", SwarmJoinRole.Manager) == null,
            "role mismatch is rejected");
    }

    static IReadOnlyDictionary<string, IReadOnlySet<string>> Descendants(
        params (string Id, string[] DescendantIds)[] entries)
    {
        return entries.ToDictionary(
            entry => entry.Id,
            entry => (IReadOnlySet<string>)new HashSet<string>(
                entry.DescendantIds,
                StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
    }

    static void Equal<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{label}: expected {expected}, actual {actual}");
    }

    static void True(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }

    static void Throws<TException>(Action action, string label)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"{label}: expected {typeof(TException).Name}");
    }

    static void Near(double expected, double actual, double tolerance, string label)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{label}: expected {expected}, actual {actual}");
    }

    static void CheckSwarmInitializationEnvironmentDefaults()
    {
        SwarmInitializationDefaults desktop = SwarmInitializationDefaults.Create(
            suggestLocalAddresses: true,
            osType: "linux",
            operatingSystem: "Docker Desktop",
            hostCandidates: new[] { "192.168.0.3" },
            daemonCandidates: new[] { "172.17.0.1" });
        Equal("172.17.0.1:2377", desktop.AdvertiseAddress, "Docker Desktop daemon-owned advertise address");
        Equal("0.0.0.0:2377", desktop.ListenAddress, "Docker Desktop wildcard listen address");
        True(desktop.IsDockerDesktopLocalDemo, "Docker Desktop is identified as local demo mode");

        SwarmInitializationDefaults unresolvedDesktop = SwarmInitializationDefaults.Create(
            suggestLocalAddresses: true,
            osType: "linux",
            operatingSystem: "Docker Desktop",
            hostCandidates: new[] { "192.168.0.3" });
        Equal(string.Empty, unresolvedDesktop.AdvertiseAddress, "Docker Desktop must not guess a host or interface address");

        SwarmInitializationDefaults native = SwarmInitializationDefaults.Create(
            suggestLocalAddresses: true,
            osType: "linux",
            operatingSystem: "Ubuntu 24.04",
            hostCandidates: new[] { "10.0.0.10" });
        Equal("10.0.0.10", native.AdvertiseAddress, "native Linux uses reachable host address");
        Equal("0.0.0.0:2377", native.ListenAddress, "native Linux wildcard listen address");
        True(!native.IsDockerDesktopLocalDemo, "native Linux is not local demo mode");

        SwarmInitializationDefaults remote = SwarmInitializationDefaults.Create(
            suggestLocalAddresses: false,
            osType: "linux",
            operatingSystem: "Docker Desktop",
            hostCandidates: new[] { "192.168.0.3" });
        Equal(string.Empty, remote.AdvertiseAddress, "remote address must be entered for the target engine");
    }

    static void CheckReadOnlyClusterRoleBindings()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "MainWindow.xaml")))
            directory = directory.Parent;

        True(directory != null, "MainWindow.xaml must be discoverable from the test output directory");
        string xaml = File.ReadAllText(Path.Combine(directory!.FullName, "MainWindow.xaml"));
        const string unsafeBinding = "<Run Text=\"{Binding RoleLabel}\"/>";
        True(!xaml.Contains(unsafeBinding, StringComparison.Ordinal),
            "Run.Text must not use its default TwoWay mode with read-only RoleLabel");
        True(xaml.CountOccurrences("<Run Text=\"{Binding RoleLabel, Mode=OneWay}\"/>") == 2,
            "Swarm and Kubernetes RoleLabel runs must both be explicitly OneWay");
        True(!xaml.Contains("Tag=\"SwarmStack\"", StringComparison.Ordinal),
            "Swarm Stack creation belongs to the bottom add button, not the resource toolbox");
        True(!xaml.Contains("<Expander Header=\"Stacks\"", StringComparison.Ordinal),
            "Swarm Stack navigation belongs to bottom tabs, not the resource sidebar");
    }
}

internal static class RegressionStringExtensions
{
    public static int CountOccurrences(this string value, string search)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }
        return count;
    }
}
