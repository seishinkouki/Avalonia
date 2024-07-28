using System;
using System.Collections.Generic;
using System.Linq;
using XamlX;
using XamlX.Ast;
using XamlX.Emit;
using XamlX.IL;
using XamlX.Transform;
using XamlX.TypeSystem;

namespace Avalonia.Markup.Xaml.XamlIl.CompilerExtensions.Transformers
{
    class XamlStyleTransformException : XamlTransformException
    {
        public XamlStyleTransformException(string message, IXamlLineInfo lineInfo, Exception innerException = null)
            : base(message, lineInfo, innerException)
        {
        }
    }

    class AvaloniaXamlIlSetterTransformer : IXamlAstTransformer
    {
        public IXamlAstNode Transform(AstTransformationContext context, IXamlAstNode node)
        {
            if (!(node is XamlAstObjectNode on
                  && on.Type.GetClrType().FullName == "Avalonia.Styling.Setter"))
                return node;

            IXamlType targetType = null;
            IXamlLineInfo lineInfo = null;

            var avaloniaTypes = context.GetAvaloniaTypes();

            var styleParent = context.ParentNodes()
                .OfType<AvaloniaXamlIlTargetTypeMetadataNode>()
                .FirstOrDefault(x => x.ScopeType == AvaloniaXamlIlTargetTypeMetadataNode.ScopeTypes.Style);

            if (styleParent != null)
            {
                targetType = styleParent.TargetType.GetClrType()
                             ?? throw new XamlStyleTransformException("Can not find parent Style Selector or ControlTemplate TargetType. If setter is not part of the style, you can set x:SetterTargetType directive on its parent.", node);
                lineInfo = on;
            }

            if (targetType == null)
            {
                throw new XamlStyleTransformException("Could not determine target type of Setter", node);
            }

            IXamlType propType = null;
            IXamlIlAvaloniaPropertyNode avaloniaPropertyNode = null;
            var property = @on.Children.OfType<XamlAstXamlPropertyValueNode>()
                .FirstOrDefault(x => x.Property.GetClrProperty().Name == "Property");
            if (property != null)
            {
                avaloniaPropertyNode = property.Values.OfType<IXamlIlAvaloniaPropertyNode>().FirstOrDefault();
                if (avaloniaPropertyNode is null)
                {
                    var propertyName = property.Values.OfType<XamlAstTextNode>().FirstOrDefault()?.Text;
                    if (propertyName == null)
                        throw new XamlStyleTransformException("Setter.Property must be a string.", node);

                    avaloniaPropertyNode = XamlIlAvaloniaPropertyHelper.CreateNode(context, propertyName,
                        new XamlAstClrTypeReference(lineInfo, targetType, false), property.Values[0]);

                    property.Values = new List<IXamlAstValueNode> {avaloniaPropertyNode};
                }

                propType = avaloniaPropertyNode.AvaloniaPropertyType;
            }
            else
            {
                var propertyPath = on.Children.OfType<XamlAstXamlPropertyValueNode>()
                    .FirstOrDefault(x => x.Property.GetClrProperty().Name == "PropertyPath");
                if (propertyPath == null)
                    throw new XamlStyleTransformException("Setter without a property or property path is not valid", node);
                if (propertyPath.Values[0] is IXamlIlPropertyPathNode ppn
                    && ppn.PropertyType != null)
                    propType = ppn.PropertyType;
                else
                    throw new XamlStyleTransformException("Unable to get the property path property type", node);
            }

            var valueProperty = on.Children
                .OfType<XamlAstXamlPropertyValueNode>()
                .FirstOrDefault(p => p.Property.GetClrProperty().Name == "Value" && p.Values.Count == 1 && p.Values[0] is XamlAstTextNode);
            var textValue = valueProperty?.Values.FirstOrDefault() as XamlAstTextNode
                            ?? on.Children.OfType<XamlAstTextNode>().FirstOrDefault();
            if (textValue is not null
                && XamlTransformHelpers.TryGetCorrectlyTypedValue(context, textValue,
                    propType, out _))
            {
                
                var setterValueProperty = new SetterValueProperty(
                    (IXamlLineInfo)valueProperty?.Property ?? textValue,
                    on.Type.GetClrType(), propType, avaloniaTypes);
                if (valueProperty is not null)
                {
                    valueProperty.Property = setterValueProperty;
                }
                else
                {
                    on.Children[on.Children.IndexOf(textValue)] =
                        new XamlAstXamlPropertyValueNode(textValue, setterValueProperty, textValue, false);
                }
            }
            // If we have `Value` property with plain text content that wasn't parsed, throw an exception.
            else if (valueProperty is not null && textValue is not null)
            {
                throw new XamlStyleTransformException($"Unable to convert property value to {propType.GetFqn()}", textValue);
            }

            // Handling a very specific case, when ITemplate value is used inside of Setter.Value,
            // Which then is materialized for a specific control, and usually would set TemplatedParent.
            // Note: this code is not always valid, as TemplatedParent might not be set,
            // but we have better validation in runtime for TemplatedBinding.
            // See Correctly_Resolve_TemplateBinding_In_Theme_Detached_Template test.
            if (!avaloniaTypes.ITemplateOfControl.IsAssignableFrom(propType)
                && on.Children.OfType<XamlAstObjectNode>()?.FirstOrDefault() is { } valueObj
                && avaloniaTypes.ITemplateOfControl.IsAssignableFrom(valueObj?.Type.GetClrType()))
            {
                on.Children[on.Children.IndexOf(valueObj)] = new AvaloniaXamlIlTargetTypeMetadataNode(valueObj,
                    new XamlAstClrTypeReference(on, targetType, false),
                    AvaloniaXamlIlTargetTypeMetadataNode.ScopeTypes.ControlTemplate);
            }

            return node;
        }

        class SetterValueProperty : XamlAstClrProperty
        {
            public SetterValueProperty(IXamlLineInfo line, IXamlType setterType, IXamlType targetType,
                AvaloniaXamlIlWellKnownTypes types)
                : base(line, "Value", setterType, null)
            {
                Getter = setterType.Methods.First(m => m.Name == "get_Value");
                var method = setterType.Methods.First(m => m.Name == "set_Value");
                Setters.Add(new XamlIlDirectCallPropertySetter(method, types.IBinding, false));
                Setters.Add(new XamlIlDirectCallPropertySetter(method, types.UnsetValueType, false));
                Setters.Add(new XamlIlDirectCallPropertySetter(method, targetType, targetType.AcceptsNull()));
            }
            
            sealed class XamlIlDirectCallPropertySetter : IXamlPropertySetter, IXamlEmitablePropertySetter<IXamlILEmitter>
            {
                private readonly IXamlMethod _method;
                private readonly IXamlType _type;
                public IXamlType TargetType { get; }
                public PropertySetterBinderParameters BinderParameters { get; }
                public IReadOnlyList<IXamlType> Parameters { get; }
                public IReadOnlyList<IXamlCustomAttribute> CustomAttributes => _method.CustomAttributes;

                public void Emit(IXamlILEmitter codegen)
                {
                    if (_type.IsValueType)
                        codegen.Box(_type);
                    codegen.EmitCall(_method, true);
                }

                public XamlIlDirectCallPropertySetter(IXamlMethod method, IXamlType type, bool allowNull)
                {
                    _method = method;
                    _type = type;
                    Parameters = new[] {type};
                    TargetType = method.ThisOrFirstParameter();
                    BinderParameters = new PropertySetterBinderParameters
                    {
                        AllowXNull = allowNull,
                        AllowRuntimeNull = allowNull
                    };
                }

                private bool Equals(XamlIlDirectCallPropertySetter other) 
                    => Equals(_method, other._method) && Equals(_type, other._type);

                public override bool Equals(object obj) 
                    => Equals(obj as XamlIlDirectCallPropertySetter);

                public override int GetHashCode() 
                    => (_method.GetHashCode() * 397) ^ _type.GetHashCode();
            }
        }
    }
}
