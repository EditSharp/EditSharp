using System;
using System.Collections.Generic;

namespace EditSharp.Editing
{
    /// <summary>
    /// What kind of control edits a property. Auto lets the type decide —
    /// see Inspect.Infer — and the rest override it where the type alone
    /// cannot tell (a float that is an angle, a string that is a path).
    /// </summary>
    public enum PropertyEditor
    {
        Auto,
        Number,
        Slider,
        Angle,
        Percent,
        Toggle,
        Text,
        Multiline,
        Dropdown,
        Color,
        Vector,
        Time,
        Media,
        Path,
        Timeline,
        List,
        Object
    }

    /// <summary>
    /// Marks a property as something an inspector or graph editor shows
    /// and edits, and says how. Opt-in: a property without this stays out
    /// of every inspector, however public it is. Everything here is a
    /// hint layered over what the property's type already says — see
    /// PropertyDescriptor for what the two resolve to together.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class EditableAttribute : Attribute
    {
        /// <summary>The label shown. Null derives one from the property name.</summary>
        public string? DisplayName { get; }

        /// <summary>Properties sharing a group are shown together under it.</summary>
        public string? Group { get; set; }

        /// <summary>Lower comes first. Equal orders keep declaration order.</summary>
        public int Order { get; set; }

        /// <summary>Range and step for numeric editors, in the property's own units. NaN leaves it unbounded.</summary>
        public double Min { get; set; } = double.NaN;
        public double Max { get; set; } = double.NaN;
        public double Step { get; set; } = double.NaN;

        /// <summary>Shown after the value — "dB", "Hz", "ms".</summary>
        public string? Unit { get; set; }

        public string? Tooltip { get; set; }

        public PropertyEditor Editor { get; set; } = PropertyEditor.Auto;

        /// <summary>Shown but not editable.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>
        /// The value a reset returns the property to. Attributes only take
        /// constants, so this covers numbers, strings, bools and enums; a
        /// property without one falls back to the value a freshly
        /// constructed object has - see <see cref="PropertyDescriptor.GetDefault"/>.
        /// </summary>
        public object? Default { get; set; }

        public EditableAttribute(string? displayName = null) => DisplayName = displayName;
    }

    /// <summary>
    /// Hides the property unless a sibling property on the same object
    /// currently holds one of the given values — a polygon's points are
    /// only worth showing while the shape is a polygon. Several of these
    /// on one property must all hold.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = true)]
    public sealed class VisibleWhenAttribute : Attribute
    {
        public string Property { get; }
        public IReadOnlyList<object?> AnyOf { get; }

        public VisibleWhenAttribute(string property, params object?[] anyOf)
        {
            Property = property;
            AnyOf = anyOf;
        }
    }

    /// <summary>
    /// An object that describes its own editable properties instead of
    /// leaving it to its type — a composite node, whose properties are
    /// whichever inner ones it chose to expose.
    /// </summary>
    public interface IInspectable
    {
        IReadOnlyList<PropertyDescriptor> Properties { get; }
    }

    /// <summary>One value a property may take, and what a picker shows for it.</summary>
    public readonly record struct Choice(object Value, string Label);

    /// <summary>An object that offers the values one of its properties may take, shown as a dropdown.</summary>
    public interface IChoiceProvider
    {
        /// <summary>Null when the property is free to take any value.</summary>
        IReadOnlyList<Choice>? ChoicesFor(string property);
    }
}
