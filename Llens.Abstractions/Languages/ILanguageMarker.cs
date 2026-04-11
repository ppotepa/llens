namespace Llens.Languages;

/// <summary>
/// Zero-cost phantom type tag. Structs implementing this mark a language identity.
/// Used to bind capabilities to a specific language at compile time.
/// </summary>
public interface ILanguageMarker { }
