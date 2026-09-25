using System;
using System.Collections.Generic;

namespace EditSharp.Editing
{
    /// <summary>Which control an editor uses for a property.</summary>
    /// <remarks><see cref="Auto"/> lets the value's type decide (see <see cref="Inspect"/>); the others override it where the type alone can't tell, such as a float that is an angle.</remarks>
    public enum PropertyEditor
    {
        /// <summary>Chosen from the value's type.</summary>
        Auto,

        /// <summary>A number field.</summary>
        Number,

        /// <summary>A number with a range; inferred when the attribute sets both Min and Max.</summary>
        Slider,

        /// <summary>A number in degrees.</summary>
        Angle,

        /// <summary>A number shown as a percentage, where 1 is 100%.</summary>
        Percent,

        /// <summary>An on/off switch for a bool.</summary>
        Toggle,

        /// <summary>A single line of text.</summary>
        Text,

        /// <summary>Text over several lines.</summary>
        Multiline,

        /// <summary>A pick from a fixed set: an enum's values, or what an <see cref="IChoiceProvider"/> offers.</summary>
        Dropdown,

        /// <summary>A colour.</summary>
        Color,

        /// <summary>An x and y pair.</summary>
        Vector,

        /// <summary>A time.</summary>
        Time,

        /// <summary>A source of any kind, shown with the source's own properties.</summary>
        Source,

        /// <summary>A file path.</summary>
        Path,

        /// <summary>A timeline in the project.</summary>
        Timeline,

        /// <summary>A list whose items are edited one by one.</summary>
        List,

        /// <summary>An object whose own editable properties are shown in place.</summary>
        Object
    }

    /// <summary>How a value measures against the frame, so an editor can show it in pixels of the render resolution.</summary>
    /// <remarks>Stored values stay fractions of the frame, which keeps a project independent of its resolution.</remarks>
    public enum FrameMeasure
    {
        /// <summary>Not measured against the frame.</summary>
        None,

        /// <summary>A length, as a fraction of the frame width.</summary>
        Width,

        /// <summary>A size: x as a fraction of the frame width, y of its height.</summary>
        Frame,

        /// <summary>A position or offset: x in half frame widths, y in half heights, up positive; 0 is the centre.</summary>
        HalfFrame,
    }

    /// <summary>Marks a property as one that inspectors and graph editors show and edit.</summary>
    /// <remarks>
    /// Opt-in: a property without it never appears in an inspector. Everything
    /// here refines what the property's type already implies;
    /// <see cref="PropertyDescriptor"/> holds what the two resolve to.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
    public sealed class EditableAttribute : Attribute
    {
        /// <summary>The label editors show; null derives one from the property name.</summary>
        public string? DisplayName { get; }

        /// <summary>Properties with the same group are shown together under that heading.</summary>
        public string? Group { get; set; }

        /// <summary>Where the property is listed: lower first, ties in declaration order.</summary>
        public int Order { get; set; }

        /// <summary>The lowest value a numeric editor allows, in the property's own units; NaN for no limit.</summary>
        public double Min { get; set; } = double.NaN;

        /// <summary>The highest value a numeric editor allows, in the property's own units; NaN for no limit.</summary>
        public double Max { get; set; } = double.NaN;

        /// <summary>How far one drag step or arrow press moves a numeric editor, in the property's own units; NaN for the editor's default.</summary>
        public double Step { get; set; } = double.NaN;

        /// <summary>Text shown after the value, such as "dB" or "Hz".</summary>
        public string? Unit { get; set; }

        /// <summary>How the value measures against the frame, for editors that can show it in pixels. Applies to a list's items too.</summary>
        public FrameMeasure Frame { get; set; }

        /// <summary>Text shown when hovering the property.</summary>
        public string? Tooltip { get; set; }

        /// <summary>The control to use instead of the one the type implies.</summary>
        public PropertyEditor Editor { get; set; } = PropertyEditor.Auto;

        /// <summary>Whether the property is shown without being editable.</summary>
        public bool ReadOnly { get; set; }

        /// <summary>The value a reset returns the property to.</summary>
        /// <remarks>Attributes only take constants, so this covers numbers, strings, bools and enums. Without one, a reset uses the value a newly constructed object has.</remarks>
        public object? Default { get; set; }

        /// <summary>Marks the property as editable.</summary>
        /// <param name="displayName">The label editors show; null derives one from the property name.</param>
        public EditableAttribute(string? displayName = null) => DisplayName = displayName;
    }

    /// <summary>Hides a property unless a sibling property currently holds one of the given values.</summary>
    /// <remarks>A polygon mask's points, for example, only show while its shape is Polygon. When a property has several of these, all must hold.</remarks>
    [AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = true)]
    public sealed class VisibleWhenAttribute : Attribute
    {
        /// <summary>The name of the sibling property to check.</summary>
        public string Property { get; }

        /// <summary>The values that make the property visible.</summary>
        public IReadOnlyList<object?> AnyOf { get; }

        /// <summary>Shows the property only while <paramref name="property"/> holds one of <paramref name="anyOf"/>.</summary>
        /// <param name="property">The name of the sibling property to check.</param>
        /// <param name="anyOf">The values that make the property visible.</param>
        public VisibleWhenAttribute(string property, params object?[] anyOf)
        {
            Property = property;
            AnyOf = anyOf;
        }
    }

    /// <summary>An object that lists its own editable properties instead of leaving it to its type.</summary>
    /// <remarks>A composite node uses this to show the inner properties it chose to expose.</remarks>
    public interface IInspectable
    {
        /// <summary>The properties editors show for this object.</summary>
        IReadOnlyList<PropertyDescriptor> Properties { get; }
    }

    /// <summary>One value a property may take, and the label a picker shows for it.</summary>
    /// <param name="Value">The value written to the property when picked.</param>
    /// <param name="Label">What the picker shows.</param>
    public readonly record struct Choice(object Value, string Label);

    /// <summary>An object that offers the values one of its properties may take, shown as a dropdown.</summary>
    public interface IChoiceProvider
    {
        /// <summary>The values <paramref name="property"/> may take right now.</summary>
        /// <param name="property">The property's name.</param>
        /// <returns>The choices, or null when the property takes any value.</returns>
        IReadOnlyList<Choice>? ChoicesFor(string property);
    }
}
