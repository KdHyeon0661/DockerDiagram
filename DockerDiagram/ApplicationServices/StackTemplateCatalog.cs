using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using DockerDiagram.Models;

namespace DockerDiagram.ApplicationServices
{
    public static class StackTemplateCatalog
    {
        private const string EmbeddedResourcePrefix = "DockerDiagram.Templates.BuiltIn.";

        public static IReadOnlyList<StackTemplateDefinition> LoadBuiltIn()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            var templates = new Dictionary<string, StackTemplateDefinition>(
                StringComparer.OrdinalIgnoreCase);

            LoadExternalTemplates(templates, options);
            LoadEmbeddedTemplates(templates, options);

            return templates.Values
                .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void LoadExternalTemplates(
            IDictionary<string, StackTemplateDefinition> templates,
            JsonSerializerOptions options)
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "Templates", "BuiltIn");
            if (!Directory.Exists(directory))
                return;

            foreach (string path in Directory.GetFiles(directory, "*.json").OrderBy(path => path))
            {
                try
                {
                    AddTemplate(
                        templates,
                        File.ReadAllText(path),
                        options,
                        overwrite: true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[StackTemplate] Failed to load '{path}': {ex.Message}");
                }
            }
        }

        private static void LoadEmbeddedTemplates(
            IDictionary<string, StackTemplateDefinition> templates,
            JsonSerializerOptions options)
        {
            Assembly assembly = typeof(StackTemplateCatalog).Assembly;
            foreach (string resourceName in assembly.GetManifestResourceNames()
                         .Where(name => name.StartsWith(
                             EmbeddedResourcePrefix,
                             StringComparison.OrdinalIgnoreCase))
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null)
                        continue;

                    using var reader = new StreamReader(stream);
                    AddTemplate(
                        templates,
                        reader.ReadToEnd(),
                        options,
                        overwrite: false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(
                        $"[StackTemplate] Failed to load embedded resource '{resourceName}': {ex.Message}");
                }
            }
        }

        private static void AddTemplate(
            IDictionary<string, StackTemplateDefinition> templates,
            string json,
            JsonSerializerOptions options,
            bool overwrite)
        {
            var template = JsonSerializer.Deserialize<StackTemplateDefinition>(json, options);
            if (template == null ||
                string.IsNullOrWhiteSpace(template.Id) ||
                string.IsNullOrWhiteSpace(template.Name))
            {
                return;
            }

            if (overwrite || !templates.ContainsKey(template.Id))
                templates[template.Id] = template;
        }
    }
}
