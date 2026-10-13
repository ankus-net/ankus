namespace Ankus.Generators;

/// <summary>
/// Shares exact managed datum conversion expressions between scalar and set dispatchers.
/// </summary>
internal static class ManagedConversion
{
    /// <summary>
    /// Reads a nullable-aware managed value from the shared native transport.
    /// </summary>
    internal static string Read(FunctionType type, string slot, string numericSuffix, bool borrowVarlena = false)
    {
        string numeric = slot + ".ReadNumeric()" + numericSuffix;
        string value = type.Managed switch
        {
            _ when type.IsBorrowedArray => slot + (borrowVarlena ? ".ReadBorrowedArray" : ".ReadOwnedArrayView") +
                (type.Element is null ? "()" : "<" + type.ElementManaged + ">()"),
            _ when type.IsMapped => slot + ".ReadMapped<" + type.Managed + ">()",
            _ when type.IsVarlena => slot + (borrowVarlena ? ".ReadVarlena<" : ".ReadOwnedVarlena<") + type.CustomType!.Managed + ">()",
            _ when type.CustomType is not null => slot + ".ReadCustom<" + type.Managed + ">()",
            _ when type.IsRaw => slot + ".ReadPolymorphic()",
            _ when type.IsInternal => slot + ".ReadInternal()",
            "global::Ankus.PgByteaView" => slot + (borrowVarlena ? ".ReadBorrowedBytea()" : ".ReadOwnedByteaView()"),
            "global::Ankus.PgTextView" => slot + (borrowVarlena ? ".ReadBorrowedText()" : ".ReadOwnedTextView()"),
            "global::Ankus.PgCStringView" => slot + (borrowVarlena ? ".ReadBorrowedCString()" : ".ReadOwnedCStringView()"),
            "global::Ankus.PgCString" => slot + ".ReadCString()",
            _ when type.IsPolymorphic => "new " + type.Managed + "(" + slot + ".ReadPolymorphic())",
            _ when type.Element?.IsRelation == true && type.IsVector => slot + ".ReadRelationVector<" + type.ElementManaged + ">()",
            _ when type.Element is not null && type.IsVector && (borrowVarlena || !type.Element.IsVarlena) =>
                slot + ".ReadVector<" + type.ElementManaged + ">()",
            _ when type.Element is not null => slot +
                (!borrowVarlena && type.Element.IsVarlena ? ".ReadCallbackArray<" : ".ReadArray<") + type.ElementManaged + ">()" +
                (type.IsVector ? ".ToVector()" : string.Empty),
            _ when type.GeometryName.Length != 0 => slot + ".Read" + type.GeometryName + "()",
            _ when type.RangeSubtype is not null => slot + ".ReadRange<" + type.RangeSubtype.Managed + ">()",
            _ when type.Enumeration is not null => "global::Ankus.PgEnums.Parse<" + type.Managed + ">(" + slot + ".ReadString())",
            _ when type.IsComposite => slot + ".ReadTuple()",
            _ when type.IsRelation => slot + ".ReadRelation()",
            "string" => slot + ".ReadString()",
            "byte[]" => slot + ".ReadBytes()",
            "global::System.Guid" => slot + ".ReadGuid()",
            "global::System.Text.Rune" => slot + ".ReadRune()",
            "global::Ankus.PgInet" => slot + ".ReadInet()",
            "global::Ankus.PgCidr" => slot + ".ReadCidr()",
            "global::System.Net.IPAddress" => slot + ".ReadInet().ToIPAddress()",
            "global::System.Net.IPNetwork" => slot + ".ReadCidr().ToIPNetwork()",
            "global::Ankus.PgJson" => slot + ".ReadJson()",
            "global::Ankus.PgJsonb" => slot + ".ReadJsonb()",
            "global::Ankus.PgNumeric" => numeric,
            "global::Ankus.PgItemPointer" => slot + ".ReadItemPointer()",
            "global::Ankus.PgTransactionId" => slot + ".ReadTransactionId()",
            "decimal" => numeric + ".ToDecimal()",
            "bool" => slot + ".Integral != 0",
            "float" => "global::System.BitConverter.Int32BitsToSingle((int)" + slot + ".Integral)",
            "double" => "global::System.BitConverter.Int64BitsToDouble(" + slot + ".Integral)",
            _ when type.IsTemporal => slot + ".Read" + type.TemporalName + "()" +
                (type.ClrTemporalName.Length == 0 ? string.Empty : ".To" + type.ClrTemporalName + "()"),
            _ => "(" + type.Managed + ")" + slot + "." + type.Field,
        };
        string absent = slot + ".IsNull != 0" + (type.Managed == "global::Ankus.PgCStringView" ? " || " + slot + ".Integral == 0" : string.Empty);
        return type.Nullable ? $"({absent} ? ({type.Managed}?)null : {value})" : value;
    }

    /// <summary>
    /// Writes a present managed value through a pointer to the shared transport.
    /// </summary>
    /// <param name="result">The result's type.</param>
    /// <param name="value">The managed expression holding the present value.</param>
    /// <param name="target">The transport pointer to write.</param>
    /// <param name="numericSuffix">The precision applied to numeric results.</param>
    /// <param name="consumedAtReturn">
    /// Whether native code copies the value as soon as the managed call returns, so text can use the thread's reusable
    /// result buffer.
    /// </param>
    internal static string Write(FunctionType result, string value, string target, string numericSuffix, bool consumedAtReturn = false)
    {
        string temporalValue = result.IsTemporal && result.ClrTemporalName.Length != 0
            ? $"global::Ankus.Pg{result.TemporalName}.From{result.ClrTemporalName}({value})" : value;
        string numericValue = (result.Managed == "decimal" ? $"global::Ankus.PgNumeric.FromDecimal({value})" : value)
            + numericSuffix;
        return result.Managed switch
        {
            _ when result.IsBorrowedArray => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromPolymorphic({value}.Datum);",
            _ when result.IsMapped => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromMapped<{result.Managed}>({value});",
            _ when result.CustomType is not null => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromCustom({value});",
            _ when result.IsRaw => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromPolymorphic({value});",
            _ when result.IsInternal => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromInternal({value});",
            _ when result.IsPolymorphic || result.IsBorrowedBuffer => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromPolymorphic({value}.Datum);",
            _ when result.Element is not null && result.IsVector &&
                result.ElementManaged is "bool" or "sbyte" or "short" or "int" or "long" or "uint" or "float" or "double" =>
                "*" + target + " = global::Ankus.CompilerServices.NativeValue.FromVector<" + result.ElementManaged + ">(" + value + ");",
            _ when result.Element is not null => "*" + target + " = global::Ankus.CompilerServices.NativeValue.FromArray(" +
                (result.IsVector ? "new global::Ankus.PgArray<" + result.ElementManaged + ">(" + value + ")" : value) + ");",
            _ when result.GeometryName.Length != 0 => $"*{target} = global::Ankus.CompilerServices.NativeValue.From{result.GeometryName}({value});",
            _ when result.RangeSubtype is not null => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromRange({value});",
            _ when result.Enumeration is not null => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromString(global::Ankus.PgEnums.GetLabel({value}));",
            _ when result.IsComposite => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromTuple({value});",
            _ when result.IsRelation => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromRelation({value});",
            "string" => $"*{target} = global::Ankus.CompilerServices.NativeValue.{(consumedAtReturn ? "FromResultString" : "FromString")}({value});",
            "byte[]" => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromBytes({value});",
            "global::Ankus.PgCString" => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromCString({value});",
            "global::System.Guid" => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromGuid({value});",
            "global::System.Text.Rune" => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromRune({value});",
            "global::Ankus.PgInet" => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromInet({value});",
            "global::Ankus.PgCidr" => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromCidr({value});",
            "global::System.Net.IPAddress" => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromInet(new global::Ankus.PgInet({value}));",
            "global::System.Net.IPNetwork" => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromCidr(new global::Ankus.PgCidr({value}));",
            "global::Ankus.PgJson" or "global::Ankus.PgJsonb" =>
                $"*{target} = global::Ankus.CompilerServices.NativeValue.FromString({value}.Text);",
            "global::Ankus.PgNumeric" or "decimal" => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromNumeric({numericValue});",
            "global::Ankus.PgItemPointer" => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromItemPointer({value});",
            "global::Ankus.PgTransactionId" => $"*{target} = global::Ankus.CompilerServices.NativeValue.FromTransactionId({value});",
            "bool" => $"{target}->Integral = {value} ? 1 : 0;",
            "float" => $"{target}->Integral = global::System.BitConverter.SingleToInt32Bits({value});",
            "double" => $"{target}->Integral = global::System.BitConverter.DoubleToInt64Bits({value});",
            _ when result.IsTemporal => $"*{target} = global::Ankus.CompilerServices.NativeValue.From{result.TemporalName}({temporalValue});",
            _ => $"{target}->{result.Field} = {value};",
        };
    }
}
