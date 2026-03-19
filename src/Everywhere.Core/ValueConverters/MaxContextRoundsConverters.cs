using Avalonia.Data.Converters;

namespace Everywhere.ValueConverters;

public static class MaxContextRoundsConverters
{
    public static IValueConverter ToDisplayKey { get; } = new BidirectionalFuncValueConverter<int, IDynamicResourceKey>(
        convert: static (value, _) => value switch
        {
            -1 => new DynamicResourceKey(LocaleKey.PersistentState_MaxContextRounds_Value_Unlimited),
            0 => new DynamicResourceKey(LocaleKey.PersistentState_MaxContextRounds_Value_CurrentInputOnly),
            _ => new DirectResourceKey(value.ToString())
        },
        convertBack: static (_, _) => throw new NotSupportedException());
}