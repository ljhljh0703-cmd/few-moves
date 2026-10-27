using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Nectorial.SlideEscape.Coop;

namespace FewMoves.Coop.Server
{
    internal sealed class CoopDefinitionCatalog
    {
        public const string LegacyDefinitionId = "coop-c1";
        private static readonly string[] AllowedIds = { "coop-c1", "coop-c2", "coop-c3" };
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public static CoopDefinitionCatalog Load(ServerOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            var catalog = new CoopDefinitionCatalog();
            for (int index = 0; index < AllowedIds.Length; index++)
            {
                string id = AllowedIds[index];
                string path = id == LegacyDefinitionId ? options.RoomPath : Path.Combine(options.RoomCatalogRoot, id + ".json");
                CoopRoomDefinition definition = ReadDefinition(path, id);
                catalog._entries.Add(id, new Entry(definition, CoopRules.RoomFingerprint(definition)));
            }
            return catalog;
        }

        public bool TryGet(string definitionId, out CoopRoomDefinition definition, out string fingerprint)
        {
            Entry entry;
            if (!_entries.TryGetValue(definitionId, out entry))
            {
                definition = null;
                fingerprint = null;
                return false;
            }
            definition = CoopRules.CloneRoom(entry.Definition);
            fingerprint = entry.Fingerprint;
            return true;
        }

        public DefinitionView[] Views()
        {
            var views = new DefinitionView[AllowedIds.Length];
            for (int index = 0; index < AllowedIds.Length; index++)
            {
                Entry entry = _entries[AllowedIds[index]];
                views[index] = new DefinitionView
                {
                    DefinitionId = entry.Definition.Id,
                    RoomResource = "CoopRooms/" + entry.Definition.Id,
                    RulesVersion = entry.Definition.RulesVersion,
                    ContentVersion = entry.Definition.ContentVersion,
                    RoomFingerprint = entry.Fingerprint
                };
            }
            return views;
        }

        private static CoopRoomDefinition ReadDefinition(string path, string expectedId)
        {
            try
            {
                CoopRoomDefinition definition = JsonSerializer.Deserialize<CoopRoomDefinition>(File.ReadAllText(path), new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true });
                if (definition == null || !string.Equals(definition.Id, expectedId, StringComparison.Ordinal) || CoopRules.ValidateRoom(definition).Length != 0)
                    throw new ServerStateCorruptException("Configured co-op definition is invalid: " + expectedId);
                return CoopRules.CloneRoom(definition);
            }
            catch (ServerStateCorruptException) { throw; }
            catch (Exception exception) when (exception is IOException || exception is JsonException || exception is NotSupportedException)
            {
                throw new ServerStateCorruptException("Configured co-op definition cannot be read: " + expectedId);
            }
        }

        private sealed class Entry
        {
            public readonly CoopRoomDefinition Definition;
            public readonly string Fingerprint;
            public Entry(CoopRoomDefinition definition, string fingerprint) { Definition = definition; Fingerprint = fingerprint; }
        }
    }
}
