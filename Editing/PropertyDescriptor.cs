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
using EditSharp.Components.Clips;
using EditSharp.History;
using SkiaSharp;

namespace EditSharp.Editing
{
    /// <summary>
    /// Everything an editor needs to know to show and change one property
    /// on one object: what to call it, what control to use, what it
    /// accepts, whether it can be keyframed, when it applies, and how to
    /// read and write it. Resolved once per type from the EditableAttribute
    /// and the property's own type (see Inspect), so a new node or clip
    /// property becomes editable by carrying the attribute and nothing
    /// else.
    ///
    /// Writes go through the property's own setter, so they are recorded
    /// for undo like any other write. List edits are recorded here, since
    /// a list's own Add/Remove are not.
    ///
    /// A descriptor can be redirected (Through) so that a composite node
    /// can present an inner node's property as its own, under its own
    /// name: the descriptor reads and writes the inner node while the
    /// inspector only ever hands it the composite.
    /// </summary>
    public sealed class PropertyDescriptor
    {
        private readonly PropertyInfo _property;
        private readonly Func<object, object>? _through;

        public string Name => _property.Name;
        public string DisplayName { get; }
        public string? Group { get; }
        public int Order { get; }
        public string? Tooltip { get; }
        public string? Unit { get; }
        public double? Min { get; }
        public double? Max { get; }
        public double? Step { get; }
        public PropertyEditor Editor { get; }
        public bool IsReadOnly { get; }

        /// <summary>The type an editor works with: Animatable&lt;T&gt; and T? both read as T.</summary>
        public Type ValueType { get; }

        /// <summary>The declared property type, before any unwrapping.</summary>
        public Type PropertyType => _property.PropertyType;

        /// <summary>True for an Animatable&lt;T&gt; — the value can carry keyframes. See GetAnimatable.</summary>
        public bool IsAnimatable { get; }

        public bool IsNullable { get; }

        /// <summary>True for a List&lt;T&gt;. ItemType/ItemEditor say what the items are; see GetList/AddItem/RemoveItem.</summary>
        public bool IsCollection { get; }
        public Type? ItemType { get; }
        public Type? ItemValueType { get; }
        public bool ItemIsAnimatable { get; }
        public PropertyEditor ItemEditor { get; }

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
            Min = double.IsNaN(attribute.Min) ? null : attribute.Min;
            Max = double.IsNaN(attribute.Max) ? null : attribute.Max;
            Step = double.IsNaN(attribute.Step) ? null : attribute.Step;
            Editor = Inspect.Infer(ValueType, attribute, IsCollection);
            IsReadOnly = attribute.ReadOnly || (!IsAnimatable && !IsCollection && !property.CanWrite);
            Conditions = conditions;

            Attribute = attribute;
        }

        internal EditableAttribute Attribute { get; }

        /// <summary>The same property, reached on another object: `resolve` turns the target an editor holds into the object that actually carries the property.</summary>
        public PropertyDescriptor Through(Func<object, object> resolve, string? alias = null, string? group = null)
        {
            Func<object, object> inner = _through;
            Func<object, object> combined = inner is null ? resolve : target => resolve(inner(target));

            return new PropertyDescriptor(_property, Attribute, Conditions, combined, alias ?? DisplayName, group ?? Group);
        }

        private object Holder(object target) => _through is null ? target : _through(target);

        // ---------------------------------------------------------------
        // Values
        // ---------------------------------------------------------------

        /// <summary>The value an editor shows: an Animatable's static value, otherwise the property itself.</summary>
        public object? GetValue(object target)
        {
            object? raw = _property.GetValue(Holder(target));

            return IsAnimatable && raw is IAnimatable animatable ? animatable.GetStaticValue() : raw;
        }

        /// <summary>Writes the value, coercing numbers and enums from whatever an editor hands over. Recorded for undo by the setter it goes through.</summary>
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

        /// <summary>The Animatable behind an animatable property, for keyframe editors. Null for anything else.</summary>
        public IAnimatable? GetAnimatable(object target)
            => IsAnimatable ? _property.GetValue(Holder(target)) as IAnimatable : null;

        /// <summary>Whether the property applies right now, per its VisibleWhen conditions.</summary>
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

        // ---------------------------------------------------------------
        // Collections
        // ---------------------------------------------------------------

        public IList? GetList(object target) => IsCollection ? _property.GetValue(Holder(target)) as IList : null;

        /// <summary>A fresh item of the list's item type, ready to add.</summary>
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

        public void AddItem(object target, object item) => InsertItem(target, GetList(target)?.Count ?? 0, item);

        public void InsertItem(object target, int index, object item)
        {
            IList list = GetList(target) ?? throw new InvalidOperationException($"{DisplayName} is not a list.");
            int at = Math.Clamp(index, 0, list.Count);

            Transaction.Apply(
                () => list.Insert(Math.Min(at, list.Count), item),
                () => list.Remove(item),
                $"add {DisplayName} item");
        }

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

        // ---------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------

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

        // "SeetheRate" -> "Seethe rate"
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

    /// <summary>
    /// Where editors ask what a thing has to edit. Of(Type) reflects the
    /// EditableAttributes once and caches the descriptors; Of(object)
    /// lets an IInspectable answer for itself instead.
    /// </summary>
    public static class Inspect
    {
        private static readonly ConcurrentDictionary<Type, IReadOnlyList<PropertyDescriptor>> _cache = new();

        public static IReadOnlyList<PropertyDescriptor> Of(object target)
            => target is IInspectable inspectable ? inspectable.Properties : Of(target.GetType());

        public static IReadOnlyList<PropertyDescriptor> Of(Type type) => _cache.GetOrAdd(type, Build);

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

            // by order, then as declared. inherited properties carry their own
            // declaring type's tokens, which is why Order exists at all
            return [.. found.OrderBy(f => f.descriptor.Order).ThenBy(f => f.token).Select(f => f.descriptor)];
        }

        /// <summary>The editor a value type gets when the attribute does not say — see PropertyEditor.</summary>
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
            if (valueType == typeof(Source)) return PropertyEditor.Media;
            if (valueType == typeof(TimelineReference) || valueType == typeof(Timeline)) return PropertyEditor.Timeline;

            // an object with editable properties of its own opens up in place
            if (Of(valueType).Count > 0) return PropertyEditor.Object;

            return PropertyEditor.Text;
        }
    }
}
