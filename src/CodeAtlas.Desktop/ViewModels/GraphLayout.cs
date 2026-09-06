namespace CodeAtlas.Desktop.ViewModels;

/// <summary>
/// Places a neighbourhood on concentric rings: the root in the middle, each hop one ring
/// further out.
/// </summary>
/// <remarks>
/// A ring layout is chosen over a force simulation because the graph is always a local
/// neighbourhood of one symbol. Distance from the centre then means exactly one thing —
/// hops from the selected symbol — and the result is deterministic, so re-running the same
/// query does not shuffle the picture under the reader.
/// </remarks>
internal static class GraphLayout
{
    /// <summary>Radius of the first ring when it is sparse enough not to need more.</summary>
    private const double BaseRadius = 200;

    /// <summary>Least distance between one ring and the next, so rings never merge.</summary>
    private const double RingGap = 240;

    /// <summary>Arc length reserved per node, which grows a ring rather than overlapping it.</summary>
    private const double NodeArc = 220;

    private const double Padding = 40;

    /// <summary>Positions every node and returns the canvas size needed to hold them.</summary>
    public static (double Width, double Height) Arrange(
        IReadOnlyList<GraphNodeViewModel> nodes,
        IReadOnlyList<GraphEdgeViewModel> edges)
    {
        if (nodes.Count == 0)
        {
            return (0, 0);
        }

        var angles = new Dictionary<long, double>();
        var neighbours = Adjacency(edges);
        var previousRadius = 0d;

        foreach (var ring in nodes.GroupBy(node => node.Depth).OrderBy(group => group.Key))
        {
            // The innermost ring is usually one node and belongs at the centre. An endpoint
            // seeds a second, so more than one is spread just far enough not to overlap
            // rather than stacked on the origin.
            if (ring.Key == 0)
            {
                var centre = ring.ToList();
                if (centre.Count == 1)
                {
                    Place(centre[0], 0, 0);
                    angles[centre[0].Id] = 0;
                    previousRadius = 0;
                    continue;
                }

                var seedRadius = RadiusFor(centre.Count);
                for (var i = 0; i < centre.Count; i++)
                {
                    var seedAngle = 2 * Math.PI * i / centre.Count;
                    angles[centre[i].Id] = seedAngle;
                    Place(centre[i], Math.Cos(seedAngle) * seedRadius, Math.Sin(seedAngle) * seedRadius);
                }

                previousRadius = seedRadius;
                continue;
            }

            // Ordering by the angle of the node that led here keeps a subtree together
            // instead of scattering it around the ring.
            var ordered = ring
                .OrderBy(node => ParentAngle(node, neighbours, angles))
                .ThenBy(node => node.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Wide enough for the ring's own nodes, and always clear of the ring inside it,
            // so a crowded inner ring pushes the outer ones out rather than colliding.
            var radius = Math.Max(
                Math.Max(BaseRadius, previousRadius + RingGap),
                RadiusFor(ordered.Count));
            previousRadius = radius;

            for (var i = 0; i < ordered.Count; i++)
            {
                // Each ring is turned slightly so nodes do not line up radially with the
                // ring inside it, which would put one node's label on another's edge.
                var angle = ((2 * Math.PI * i) / ordered.Count) + (ring.Key * 0.4);
                angles[ordered[i].Id] = angle;
                Place(ordered[i], Math.Cos(angle) * radius, Math.Sin(angle) * radius);
            }
        }

        return Normalise(nodes);
    }

    /// <summary>
    /// The radius at which <paramref name="count"/> nodes sit <see cref="NodeArc"/> apart.
    /// </summary>
    /// <remarks>
    /// Measured along the chord between neighbours rather than the arc: for a ring of two
    /// or three the arc badly overestimates how far apart they actually are, and they would
    /// overlap. The two converge as the ring fills.
    /// </remarks>
    private static double RadiusFor(int count) =>
        count <= 1 ? 0 : NodeArc / (2 * Math.Sin(Math.PI / count));

    private static void Place(GraphNodeViewModel node, double centreX, double centreY)
    {
        node.X = centreX - (GraphNodeViewModel.Width / 2);
        node.Y = centreY - (GraphNodeViewModel.Height / 2);
    }

    private static Dictionary<long, List<GraphNodeViewModel>> Adjacency(IReadOnlyList<GraphEdgeViewModel> edges)
    {
        var map = new Dictionary<long, List<GraphNodeViewModel>>();

        foreach (var edge in edges)
        {
            Link(edge.Source.Id, edge.Target);
            Link(edge.Target.Id, edge.Source);
        }

        return map;

        void Link(long id, GraphNodeViewModel other)
        {
            if (!map.TryGetValue(id, out var list))
            {
                map[id] = list = [];
            }

            list.Add(other);
        }
    }

    /// <summary>The angle of the neighbour one ring closer in, or 0 when there is none.</summary>
    private static double ParentAngle(
        GraphNodeViewModel node,
        Dictionary<long, List<GraphNodeViewModel>> neighbours,
        Dictionary<long, double> angles)
    {
        if (!neighbours.TryGetValue(node.Id, out var adjacent))
        {
            return 0;
        }

        foreach (var candidate in adjacent)
        {
            if (candidate.Depth == node.Depth - 1 && angles.TryGetValue(candidate.Id, out var angle))
            {
                return angle;
            }
        }

        return 0;
    }

    /// <summary>Shifts everything into positive space so the canvas can be sized to fit.</summary>
    private static (double Width, double Height) Normalise(IReadOnlyList<GraphNodeViewModel> nodes)
    {
        var minX = nodes.Min(node => node.X);
        var minY = nodes.Min(node => node.Y);

        foreach (var node in nodes)
        {
            node.X += Padding - minX;
            node.Y += Padding - minY;
        }

        return (
            nodes.Max(node => node.X) + GraphNodeViewModel.Width + Padding,
            nodes.Max(node => node.Y) + GraphNodeViewModel.Height + Padding);
    }
}
