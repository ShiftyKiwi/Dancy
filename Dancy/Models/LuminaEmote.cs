namespace Dancy.Core.Models
{
    /// <summary>
    /// Simple DTO for an emote we can show in the UI and use for overrides.
    /// </summary>
    public class LuminaEmote
    {
        /// <summary>
        /// Display name, e.g. "Bee's Knees".
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Command string, e.g. "/beesknees".
        /// </summary>
        public string Command { get; set; } = string.Empty;

        /// <summary>
        /// RowId from the Lumina Emote sheet.
        /// </summary>
        public uint RowId { get; set; }

        /// <summary>
        /// Primary timeline key we want to use for PAP overrides,
        /// usually something like "emote/dance16_loop".
        /// </summary>
        public string PrimaryTimelineKey { get; set; } = string.Empty;

        /// <summary>
        /// Optional: Emote category name (General, Special, etc.).
        /// </summary>
        public string Category { get; set; } = string.Empty;
    }
}
