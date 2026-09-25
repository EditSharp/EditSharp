using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using EditSharp.Components;
using EditSharp.Components.Sources;
using EditSharp.Components.Clips;
using EditSharp.History;
using SkiaSharp;

namespace EditSharp.Editing
{
    /// <summary>Everything an editor needs to show and change one editable property: its label, control, limits, and how to read and write it.</summary>
    /// <remarks>
    /// Built once per type from <see cref="EditableAttribute"/> and the property's
    /// own type (see <see cref="Inspect"/>). Writes go through the property's
    /// setter, so they are recorded for undo; list edits are recorded here. A
    /// descriptor made with <see cref="Through"/> reads and writes a property on
    /// another object, which is how a composite node shows an inner node's
    /// property as its own.
    /// </remarks>
    public sealed class PropertyDescriptor
    {
        private readonly PropertyInfo _property;
        private readonly Func<object, object>? _through;

        /// <summary>The property's name in code.</summary>
        public string Name => _property.Name;

        /// <summary>The label editors show.</summary>
        public string DisplayName { get; }

        /// <summary>The heading the property is shown under, if any.</summary>
        public string? Group { get; }

        /// <summary>Where the property is listed: lower first.</summary>
        public int Order { get; }

        /// <summary>Text shown when hovering the property.</summary>
        public string? Tooltip { get; }

        /// <summary>Text shown after the value, such as "dB".</summary>
        public string? Unit { get; }

        /// <summary>How the value measures against the frame, for editors that can show it in pixels.</summary>
        public FrameMeasure Frame { get; }

        /// <summary>The lowest value a numeric editor allows; null for no limit.</summary>
        public double? Min { get; }

        /// <summary>The highest value a numeric editor allows; null for no limit.</summary>
        public double? Max { get; }

        /// <summary>How far one step moves a numeric editor; null for the editor's default.</summary>
        public double? Step { get; }

        /// <summary>The control to use, resolved from the attribute and the value's type.</summary>
        public PropertyEditor Editor { get; }

        /// <summary>Whether the property can be shown but not changed.</summary>
        public bool IsReadOnly { get; }

        /// <summary>The type an editor works with: <c>Animatable&lt;T&gt;</c> and <c>T?</c> both read as <c>T</c>.</summary>
        public Type ValueType { get; }

        /// <summary>The property's declared type.</summary>
        public Type PropertyType => _property.PropertyType;

        /// <summary>Whether the property is an <c>Animatable&lt;T&gt;</c> and can carry keyframes.</summary>
        public bool IsAnimatable { get; }

        /// <summary>Whether the property is a nullable value type, so null is a valid value.</summary>
        public bool IsNullable { get; }

        /// <summary>Whether the property is a <c>List&lt;T&gt;</c>.</summary>
        public bool IsCollection { get; }

        /// <summary>A list's item type as declared; null when not a list.</summary>
        public Type? ItemType { get; }

        /// <summary>The type an editor works with for each item; null when not a list.</summary>
        public Type? ItemValueType { get; }

        /// <summary>Whether each item is an <c>Animatable&lt;T&gt;</c>.</summary>
        public bool ItemIsAnimatable { get; }

        /// <summary>The control used for each item.</summary>
        public PropertyEditor ItemEditor { get; }

        /// <summary>The conditions that must all hold for the property to show.</summary>
        public IReadOnlyList<VisibleWhenAttribute> Conditions { get; }

        internal PropertyDescriptor(PropertyInfo property, EditableAttribute attribute, IReadOnlyList<VisibleWhenAttribute> conditions)
            : this(property, attribute, conditions, null, null, null) { }

        private PropertyDescriptor(
            PropertyInfo property, EditableAttribute attribute, IReadOnlyList<VisibleWhenAttribute> conditions,
            Func<object, object>? through, string? alias, string? group)
        {
            _property = property;
            _through = through;

            (ValueType, IsAnimatable, IsNullable, IsCollection, ItemType) = Unwrap(property.PropertyType);

            if (ItemType is not null)
            {
                (ItemValueType, ItemIsAnimatable, _, _, _) = Unwrap(ItemType);
                ItemEditor = Inspect.Infer(ItemValueType!, attribute, collection: false);
            }

            DisplayName = alias ?? attribute.DisplayName ?? Humanize(property.Name);
            Group = group ?? attribute.Group;
            Order = attribute.Order;
            Tooltip = attribute.Tooltip;
            Unit = attribute.Unit;
            Frame = attribute.Frame;
            Min = double.IsNaN(attribute.Min) ? null : attribute.Min;
            Max = double.IsNaN(attribute.Max) ? null : attribute.Max;
            Step = double.IsNaN(attribute.Step) ? null : attribute.Step;
            Editor = Inspect.Infer(ValueType, attribute, IsCollection);
            IsReadOnly = attribute.ReadOnly || (!IsAnimatable && !IsCollection && !property.CanWrite);
            Conditions = conditions;

            Attribute = attribute;
        }

        internal EditableAttribute Attribute { get; }

        /// <summary>The same property, reached through another object.</summary>
        /// <param name="resolve">Turns the object an editor holds into the object that carries the property.</param>
        /// <param name="alias">The label to show instead; null keeps this one's.</param>
        /// <param name="group">The heading to show it under instead; null keeps this one's.</param>
        /// <returns>A descriptor that reads and writes through <paramref name="resolve"/>.</returns>
        public PropertyDescriptor Through(Func<object, object> resolve, string? alias = null, string? group = null)
        {
            Func<object, object> inner = _through;
            Func<object, object> combined = inner is null ? resolve : target => resolve(inner(target));

            return new PropertyDescriptor(_property, Attribute, Conditions, combined, alias ?? DisplayName, group ?? Group);
        }

        private object Holder(object target) => _through is null ? target : _through(target);

        // ---- values ----

        /// <summary>The value an editor shows.</summary>
        /// <param name="target">The object the property belongs to.</param>
        /// <returns>An animatable property's static value, otherwise the property's value.</returns>
        public object? GetValue(object target)
        {
            object? raw = _property.GetValue(Holder(target));

            return IsAnimatable && raw is IAnimatable animatable ? animatable.GetStaticValue() : raw;
        }

        /// <summary>Writes a value, converting numbers and enum names to the property's type. Recorded for undo.</summary>
        /// <param name="target">The object the property belongs to.</param>
        /// <param name="value">The new value.</param>
        /// <exception cref="InvalidOperationException">The property is read-only, or an animatable property has no Animatable behind it.</exception>
        public void SetValue(object target, object? value)
        {
            if (IsReadOnly) throw new InvalidOperationException($"{DisplayName} is read-only.");

            object holder = Holder(target);
            object? coerced = Coerce(value, ValueType);

            if (IsAnimatable)
            {
                if (_property.GetValue(holder) is not IAnimatable animatable)
                    throw new InvalidOperationException($"{DisplayName} has no Animatable behind it.");

                animatable.SetStaticValue(coerced);
                return;
            }

            _property.SetValue(holder, coerced);
        }

        /// <summary>The Animatable behind an animatable property, for keyframe editing.</summary>
        /// <param name="target">The object the property belongs to.</param>
        /// <returns>The Animatable, or null when the property isn't animatable.</returns>
        public IAnimatable? GetAnimatable(object target)
            => IsAnimatable ? _property.GetValue(Holder(target)) as IAnimatable : null;

        // ---- defaults ----

        /// <summary>The value a reset returns the property to on <paramref name="target"/>.</summary>
        /// <remarks>The attribute's Default when it has one, otherwise the value the property has on a newly constructed object of the same type.</remarks>
        /// <param name="target">The object the property belongs to.</param>
        /// <param name="value">The default, when there is one.</param>
        /// <returns>False when there is no default, such as for an abstract type with no attribute default.</returns>
        public bool TryGetDefault(object target, out object? value)
        {
            if (Attribute.Default is not null)
            {
                value = Coerce(Attribute.Default, ValueType);
                return true;
            }

            object holder = Holder(target);
            object? prototype = Prototype.Of(holder.GetType());

            if (prototype is null)
            {
                value = null;
                return false;
            }

            object? raw = _property.GetValue(prototype);
            value = IsAnimatable && raw is IAnimatable animatable ? animatable.GetStaticValue() : raw;
            return true;
        }

        /// <summary>Whether the property holds its default on <paramref name="target"/>.</summary>
        /// <param name="target">The object the property belongs to.</param>
        /// <returns>False when it differs, or there is no default.</returns>
        public bool IsDefault(object target)
            => TryGetDefault(target, out object? expected) && Equals(GetValue(target), expected);

        //one untouched instance per type, for reading the values a type starts with; null for types that can't be made bare
        private static class Prototype
        {
            private static readonly ConcurrentDictionary<Type, object?> _cache = new();

            public static object? Of(Type type) => _cache.GetOrAdd(type, Make);

            private static object? Make(Type type)
            {
                if (type.IsAbstract || type.IsInterface) return null;

                try
                {
                    using var _ = Transaction.Suppress();
                    return Activator.CreateInstance(type, nonPublic: true);
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>Whether the property applies right now, per its <see cref="Conditions"/>.</summary>
        /// <param name="target">The object the property belongs to.</param>
        /// <returns>True when every condition holds.</returns>
        public bool IsVisible(object target)
        {
            if (Conditions.Count == 0) return true;

            object holder = Holder(target);
            Type type = holder.GetType();

            foreach (VisibleWhenAttribute condition in Conditions)
            {
                PropertyInfo? sibling = type.GetProperty(condition.Property, BindingFlags.Public | BindingFlags.Instance);
                if (sibling is null) return false;

                object? current = sibling.GetValue(holder);
                if (current is IAnimatable animatable) current = animatable.GetStaticValue();

                if (!condition.AnyOf.Any(v => Equals(v, current))) return false;
            }

            return true;
        }

        // ---- lists ----

        /// <summary>The list behind a list property.</summary>
        /// <param name="target">The object the property belongs to.</param>
        /// <returns>The list, or null when the property isn't a list.</returns>
        public IList? GetList(object target) => IsCollection ? _property.GetValue(Holder(target)) as IList : null;

        /// <summary>A new item of the list's item type, ready to add.</summary>
        /// <returns>The item; an animatable item starts at its type's default value.</returns>
        /// <exception cref="InvalidOperationException">The property isn't a list.</exception>
        public object CreateItem()
        {
            if (ItemType is null) throw new InvalidOperationException($"{DisplayName} is not a list.");

            if (ItemIsAnimatable)
            {
                Type inner = ItemValueType!;
                object? seed = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return Activator.CreateInstance(ItemType, seed)!;
            }

            return Activator.CreateInstance(ItemType)!;
        }

        /// <summary>Adds an item to the end of the list. Recorded for undo.</summary>
        /// <param name="target">The object the property belongs to.</param>
        /// <param name="item">The item to add.</param>
        /// <exception cref="InvalidOperationException">The property isn't a list.</exception>
        public void AddItem(object target, object item) => InsertItem(target, GetList(target)?.Count ?? 0, item);

        /// <summary>Inserts an item into the list. Recorded for undo.</summary>
        /// <param name="target">The object the property belongs to.</param>
        /// <param name="index">Where to insert; clamped to the list's bounds.</param>
        /// <param name="item">The item to insert.</param>
        /// <exception cref="InvalidOperationException">The property isn't a list.</exception>
        public void InsertItem(object target, int index, object item)
        {
            IList list = GetList(target) ?? throw new InvalidOperationException($"{DisplayName} is not a list.");
            int at = Math.Clamp(index, 0, list.Count);

            Transaction.Apply(
                () => list.Insert(Math.Min(at, list.Count), item),
                () => list.Remove(item),
                $"add {DisplayName} item");
        }

        /// <summary>Removes an item from the list. Recorded for undo.</summary>
        /// <param name="target">The object the property belongs to.</param>
        /// <param name="index">The item's position; nothing happens when it's out of range.</param>
        /// <exception cref="InvalidOperationException">The property isn't a list.</exception>
        public void RemoveItem(object target, int index)
        {
            IList list = GetList(target) ?? throw new InvalidOperationException($"{DisplayName} is not a list.");
            if (index < 0 || index >= list.Count) return;

            object? item = list[index];

            Transaction.Apply(
                () => list.Remove(item),
                () => list.Insert(Math.Min(index, list.Count), item),
                $"remove {DisplayName} item");
        }

        // ---- helpers ----

        private static (Type valueType, bool animatable, bool nullable, bool collection, Type? itemType) Unwrap(Type type)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Animatable<>))
                return (type.GetGenericArguments()[0], true, false, false, null);

            Type? underlying = Nullable.GetUnderlyingType(type);
            if (underlying is not null) return (underlying, false, true, false, null);

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
                return (type, false, false, true, type.GetGenericArguments()[0]);

            return (type, false, false, false, null);
        }

        internal static object? Coerce(object? value, Type target)
        {
            if (value is null || target.IsInstanceOfType(value)) return value;

            if (target.IsEnum)
                return value is string name ? Enum.Parse(target, name, ignoreCase: true) : Enum.ToObject(target, value);

            if (value is IConvertible) return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);

            return value;
        }

        //"SeetheRate" -> "Seethe rate"
        private static string Humanize(string name)
        {
            StringBuilder text = new(name.Length + 4);

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];

                if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
                {
                    text.Append(' ');
                    text.Append(char.ToLowerInvariant(c));
                }
                else text.Append(c);
            }

            return text.ToString();
        }
    }

    /// <summary>Finds what an object or type has to edit.</summary>
    /// <remarks>Descriptors for a type are read from its <see cref="EditableAttribute"/>s once and cached; an <see cref="IInspectable"/> object lists its own instead.</remarks>
    public static class Inspect
    {
        private static readonly ConcurrentDictionary<Type, IReadOnlyList<PropertyDescriptor>> _cache = new();

        /// <summary>The editable properties of an object.</summary>
        /// <param name="target">The object.</param>
        /// <returns>Its descriptors, ordered by Order then declaration.</returns>
        public static IReadOnlyList<PropertyDescriptor> Of(object target)
            => target is IInspectable inspectable ? inspectable.Properties : Of(target.GetType());

        /// <summary>The editable properties a type declares or inherits.</summary>
        /// <param name="type">The type.</param>
        /// <returns>Its descriptors, ordered by Order then declaration.</returns>
        public static IReadOnlyList<PropertyDescriptor> Of(Type type) => _cache.GetOrAdd(type, Build);

        /// <summary>One editable property of an object, by name.</summary>
        /// <param name="target">The object.</param>
        /// <param name="name">The property's name in code.</param>
        /// <returns>The descriptor, or null when the object has no editable property by that name.</returns>
        public static PropertyDescriptor? Find(object target, string name) => Of(target).FirstOrDefault(d => d.Name == name);

        private static IReadOnlyList<PropertyDescriptor> Build(Type type)
        {
            List<(PropertyDescriptor descriptor, int token)> found = [];

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                EditableAttribute? editable = property.GetCustomAttribute<EditableAttribute>(inherit: true);
                if (editable is null) continue;

                List<VisibleWhenAttribute> conditions = [.. property.GetCustomAttributes<VisibleWhenAttribute>(inherit: true)];

                found.Add((new PropertyDescriptor(property, editable, conditions), property.MetadataToken));
            }

            //by order, then as declared. inherited properties carry their own
            //declaring type's tokens, which is why Order exists at all
            return [.. found.OrderBy(f => f.descriptor.Order).ThenBy(f => f.token).Select(f => f.descriptor)];
        }

        //the editor a value type gets when the attribute doesn't say
        internal static PropertyEditor Infer(Type valueType, EditableAttribute attribute, bool collection)
        {
            if (attribute.Editor != PropertyEditor.Auto && !collection) return attribute.Editor;
            if (collection) return PropertyEditor.List;

            if (valueType == typeof(bool)) return PropertyEditor.Toggle;
            if (valueType.IsEnum) return PropertyEditor.Dropdown;
            if (valueType == typeof(string)) return PropertyEditor.Text;

            if (valueType == typeof(float) || valueType == typeof(double) || valueType == typeof(int) || valueType == typeof(long))
                return !double.IsNaN(attribute.Min) && !double.IsNaN(attribute.Max) ? PropertyEditor.Slider : PropertyEditor.Number;

            if (valueType == typeof(SKColor)) return PropertyEditor.Color;
            if (valueType == typeof(Vector2)) return PropertyEditor.Vector;
            if (valueType == typeof(TimeSpan)) return PropertyEditor.Time;
            if (typeof(Source).IsAssignableFrom(valueType)) return PropertyEditor.Source;
            if (valueType == typeof(Timeline)) return PropertyEditor.Timeline;

            //an object with editable properties of its own opens up in place
            if (Of(valueType).Count > 0) return PropertyEditor.Object;

            return PropertyEditor.Text;
        }
    }
}
