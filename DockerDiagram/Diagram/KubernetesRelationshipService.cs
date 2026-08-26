using DockerDiagram.Models;
using DockerDiagram.ViewModels;
using Newtonsoft.Json.Linq;

namespace DockerDiagram.Diagram
{
    public sealed class KubernetesRelationshipService
    {
        public void RefreshRelationships(SheetViewModel sheet)
        {
            if (sheet.RuntimeKind != RuntimeKind.Kubernetes)
                return;

            var kubernetesNodes = sheet.Nodes
                .Where(node => node.IsKubernetesResource)
                .ToList();
            if (kubernetesNodes.Count < 2)
                return;

            var metadata = kubernetesNodes
                .Select(node => new KubernetesNodeMetadata(node, TryParseJson(node.KubernetesPodJsonText)))
                .ToList();

            foreach (var source in metadata)
            {
                AddOwnerRelationships(sheet, source, metadata);
                AddServiceSelectorRelationships(sheet, source, metadata);
                AddPersistentVolumeClaimRelationships(sheet, source, metadata);
            }

            foreach (var node in kubernetesNodes)
                node.RefreshConnections();
        }

        private void AddOwnerRelationships(
            SheetViewModel sheet,
            KubernetesNodeMetadata source,
            IReadOnlyCollection<KubernetesNodeMetadata> all)
        {
            foreach (var owner in source.OwnerReferences)
            {
                var ownerNode = all.FirstOrDefault(candidate =>
                    candidate.Kind.Equals(owner.Kind, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Name.Equals(owner.Name, StringComparison.OrdinalIgnoreCase) &&
                    IsSameNamespaceOrClusterScoped(candidate, source));

                if (ownerNode == null)
                    continue;

                sheet.TryAddDirectedConnection(
                    ownerNode.Node,
                    source.Node,
                    RelationType.KubernetesOwner);
            }
        }

        private void AddServiceSelectorRelationships(
            SheetViewModel sheet,
            KubernetesNodeMetadata source,
            IReadOnlyCollection<KubernetesNodeMetadata> all)
        {
            if (!source.Kind.Equals("Service", StringComparison.OrdinalIgnoreCase) ||
                source.SelectorLabels.Count == 0)
            {
                return;
            }

            foreach (var pod in all.Where(candidate =>
                         candidate.Node.IsKubernetesPod &&
                         candidate.Namespace.Equals(source.Namespace, StringComparison.OrdinalIgnoreCase)))
            {
                bool matches = source.SelectorLabels.All(selector =>
                    pod.Labels.TryGetValue(selector.Key, out string? value) &&
                    value.Equals(selector.Value, StringComparison.OrdinalIgnoreCase));

                if (!matches)
                    continue;

                sheet.TryAddDirectedConnection(
                    source.Node,
                    pod.Node,
                    RelationType.KubernetesSelector,
                    ipAddress: string.Join(", ", source.SelectorLabels.Select(kv => $"{kv.Key}={kv.Value}")));
            }
        }

        private void AddPersistentVolumeClaimRelationships(
            SheetViewModel sheet,
            KubernetesNodeMetadata source,
            IReadOnlyCollection<KubernetesNodeMetadata> all)
        {
            if (!source.Node.IsKubernetesPod || source.PersistentVolumeClaims.Count == 0)
                return;

            foreach (string claimName in source.PersistentVolumeClaims)
            {
                var pvc = all.FirstOrDefault(candidate =>
                    candidate.Kind.Equals("PVC", StringComparison.OrdinalIgnoreCase) &&
                    candidate.Namespace.Equals(source.Namespace, StringComparison.OrdinalIgnoreCase) &&
                    candidate.Name.Equals(claimName, StringComparison.OrdinalIgnoreCase));

                if (pvc == null)
                    continue;

                sheet.TryAddDirectedConnection(
                    source.Node,
                    pvc.Node,
                    RelationType.KubernetesVolumeClaim,
                    mountPath: claimName);
            }
        }

        private static JObject? TryParseJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                return JObject.Parse(json);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSameNamespaceOrClusterScoped(KubernetesNodeMetadata owner, KubernetesNodeMetadata child)
        {
            return string.IsNullOrWhiteSpace(owner.Namespace) ||
                   string.IsNullOrWhiteSpace(child.Namespace) ||
                   owner.Namespace.Equals(child.Namespace, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class KubernetesNodeMetadata
        {
            public KubernetesNodeMetadata(NodeViewModel node, JObject? raw)
            {
                Node = node;
                Raw = raw;
                Kind = string.IsNullOrWhiteSpace(node.KubernetesKind) ? "Pod" : node.KubernetesKind;
                Namespace = node.KubernetesNamespace;
                Name = node.KubernetesResourceName;
                Labels = ReadStringMap(raw?["metadata"]?["labels"] as JObject);
                SelectorLabels = ReadStringMap(raw?["spec"]?["selector"] as JObject);
                OwnerReferences = ReadOwnerReferences(raw);
                PersistentVolumeClaims = ReadPersistentVolumeClaims(raw);
            }

            public NodeViewModel Node { get; }
            public JObject? Raw { get; }
            public string Kind { get; }
            public string Namespace { get; }
            public string Name { get; }
            public Dictionary<string, string> Labels { get; }
            public Dictionary<string, string> SelectorLabels { get; }
            public List<KubernetesOwnerReference> OwnerReferences { get; }
            public List<string> PersistentVolumeClaims { get; }

            private static Dictionary<string, string> ReadStringMap(JObject? source)
            {
                return source?.Properties()
                    .Where(property => property.Value.Type != JTokenType.Null)
                    .ToDictionary(
                        property => property.Name,
                        property => property.Value.ToString(),
                        StringComparer.OrdinalIgnoreCase)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            private static List<KubernetesOwnerReference> ReadOwnerReferences(JObject? raw)
            {
                return raw?["metadata"]?["ownerReferences"]?
                    .OfType<JObject>()
                    .Select(owner => new KubernetesOwnerReference(
                        owner["kind"]?.ToString() ?? string.Empty,
                        owner["name"]?.ToString() ?? string.Empty))
                    .Where(owner => !string.IsNullOrWhiteSpace(owner.Kind) && !string.IsNullOrWhiteSpace(owner.Name))
                    .ToList()
                    ?? [];
            }

            private static List<string> ReadPersistentVolumeClaims(JObject? raw)
            {
                return raw?["spec"]?["volumes"]?
                    .OfType<JObject>()
                    .Select(volume => volume["persistentVolumeClaim"]?["claimName"]?.ToString())
                    .Where(claimName => !string.IsNullOrWhiteSpace(claimName))
                    .Select(claimName => claimName!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    ?? [];
            }
        }

        private sealed record KubernetesOwnerReference(string Kind, string Name);
    }
}
