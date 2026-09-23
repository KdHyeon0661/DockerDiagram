using DockerDiagram.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DockerDiagram.Infrastructure
{
    public static class SwarmJoinVerification
    {
        public static bool HasExpectedRole(SwarmClusterState state, SwarmJoinRole role)
        {
            ArgumentNullException.ThrowIfNull(state);
            return role == SwarmJoinRole.Manager ? state.IsManager : state.IsWorker;
        }

        public static DockerSwarmNode? FindJoinedNode(
            IEnumerable<DockerSwarmNode> nodes,
            string nodeId,
            string advertiseAddress,
            SwarmJoinRole role)
        {
            ArgumentNullException.ThrowIfNull(nodes);
            string expectedRole = role == SwarmJoinRole.Manager ? "manager" : "worker";
            DockerSwarmNode[] candidates = nodes
                .Where(node => node.Role.Equals(expectedRole, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (!string.IsNullOrWhiteSpace(nodeId))
            {
                return candidates.FirstOrDefault(node =>
                    node.Id.Equals(nodeId.Trim(), StringComparison.OrdinalIgnoreCase));
            }

            string expectedAddress = NormalizeAddress(advertiseAddress);
            if (string.IsNullOrWhiteSpace(expectedAddress)) return null;
            return candidates.FirstOrDefault(node =>
                NormalizeAddress(node.Address).Equals(expectedAddress, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeAddress(string? value)
        {
            string address = (value ?? string.Empty).Trim();
            if (address.StartsWith('['))
            {
                int closingBracket = address.IndexOf(']');
                return closingBracket > 1 ? address[1..closingBracket] : address;
            }

            int colonCount = address.Count(character => character == ':');
            if (colonCount == 1)
            {
                int colon = address.LastIndexOf(':');
                if (colon > 0 && int.TryParse(address[(colon + 1)..], out _))
                    return address[..colon];
            }

            return address;
        }
    }
}
