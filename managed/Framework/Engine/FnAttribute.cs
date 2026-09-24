namespace CX.Framework;

/// <summary>The engine function's signature, <c>"R(T name, ...)"</c>, declared once on its <see cref="SymbolMap"/>
/// symbol. <c>tools/engine/views.py</c> emits the typed pointer, the typed hook installs and, for a first
/// parameter <c>View this</c>, the view method. <c>sret(View this, ...)</c> marks a class returned by value.</summary>
[System.AttributeUsage(System.AttributeTargets.Field)]
internal sealed class FnAttribute(string signature) : System.Attribute
{
    public string Signature { get; } = signature;
}
