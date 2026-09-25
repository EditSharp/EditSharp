namespace EditSharp.Editing
{
    /// <summary>An object whose reset defaults depend on its own state, such as a length read from a file.</summary>
    /// <remarks><see cref="PropertyDescriptor.TryGetDefault"/> asks this before the attribute's Default or the prototype.</remarks>
    public interface IPropertyDefaults
    {
        /// <summary>The value a reset returns <paramref name="propertyName"/> to on this object.</summary>
        /// <param name="propertyName">The property's name.</param>
        /// <param name="value">The default, when this object supplies one.</param>
        /// <returns>False to fall back to the attribute's Default or the prototype.</returns>
        bool TryGetDefault(string propertyName, out object? value);
    }
}
