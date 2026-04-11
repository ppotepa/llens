namespace Llens.Languages;

/// <summary>
/// Marker base for all language-specific capability plugins.
/// Binds a capability implementation to a specific language at compile time,
/// preventing e.g. a Rust extractor from being registered under a C# language.
/// </summary>
public interface ILanguageCapability<TLanguage> where TLanguage : ILanguageMarker { }
