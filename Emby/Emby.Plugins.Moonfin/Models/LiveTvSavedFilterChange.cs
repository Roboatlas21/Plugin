using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Emby.Plugins.Moonfin.Models
{

    /// <summary>An intentional atomic change to shared guide shortcuts.</summary>
    public sealed class LiveTvSavedFilterChange
    {
        [JsonPropertyName("action")] public string? Action { get; set; }
        [JsonPropertyName("filter")] public string? Filter { get; set; }
        [JsonPropertyName("filters")] public List<string>? Filters { get; set; }

        private static bool ValidFilter(string? value) =>
            value != null && value.Length <= 1024 &&
            ((value.StartsWith("group:", StringComparison.Ordinal) && value.Length > 6) ||
             (value.StartsWith("category:", StringComparison.Ordinal) && value.Length > 9));

        public bool IsValid() => Action switch
        {
            "add" or "remove" => ValidFilter(Filter),
            "initializeIfAbsent" => Filters is { Count: <= 1000 } && Filters.All(ValidFilter),
            _ => false,
        };
    }
}
