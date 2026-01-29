using System.Collections.Immutable;
using System.Globalization;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Everywhere.I18N.SourceGenerator;

[Generator]
public class I18NSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Register the additional file provider for RESX files
        var resxFiles = context.AdditionalTextsProvider
            .Where(file => Path.GetExtension(file.Path).Equals(".resx", StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(file.Path).StartsWith("Strings.", StringComparison.OrdinalIgnoreCase) &&
                Path.GetDirectoryName(file.Path)?.EndsWith("I18N", StringComparison.OrdinalIgnoreCase) == true)
            .Collect();

        // Register the output source
        context.RegisterSourceOutput(resxFiles, GenerateI18NCode);
    }

    private static void GenerateI18NCode(SourceProductionContext context, ImmutableArray<AdditionalText> resxFiles)
    {
        if (resxFiles.Length == 0)
        {
            return;
        }

        try
        {
            // Group RESX files by base name and locale
            var defaultResxFile = resxFiles.FirstOrDefault(f => Path.GetFileName(f.Path).Equals("Strings.resx", StringComparison.OrdinalIgnoreCase));
            if (defaultResxFile == null)
            {
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        new DiagnosticDescriptor(
                            "I18N001",
                            "Missing Default RESX File",
                            "Could not find the default Strings.resx file in I18N directory",
                            "I18N",
                            DiagnosticSeverity.Error,
                            isEnabledByDefault: true),
                        Location.None));
                return;
            }

            // Parse the default RESX to get all keys
            var defaultContent = defaultResxFile.GetText(context.CancellationToken)?.ToString();
            if (string.IsNullOrEmpty(defaultContent))
            {
                return;
            }

            // Parse default RESX for keys and values
            var defaultEntries = ParseResxEntries(defaultContent!);
            if (defaultEntries.Count == 0)
            {
                return;
            }

            // Generate LocaleKey.g.cs based on all available keys
            var localeKeySource = GenerateLocaleKeyClass(defaultResxFile.Path, defaultEntries);
            context.AddSource("LocaleKey.g.cs", SourceText.From(localeKeySource, Encoding.UTF8));

            // Generate LocaleResolver.g.cs based on all available keys
            var localeResolverSource = GenerateLocaleResolverClass(defaultResxFile.Path, defaultEntries);
            context.AddSource("LocaleResolver.g.cs", SourceText.From(localeResolverSource, Encoding.UTF8));

            // A map for locale name to enum locale name
            // Default -> default
            // En -> default
            // De -> de
            // ZhHans -> zh-hans
            var localeNamesMap = new Dictionary<string, string>
            {
                { "En", "default" }
            };

            foreach (var resxFile in resxFiles)
            {
                if (Path.GetFileName(resxFile.Path).Equals("Strings.resx", StringComparison.OrdinalIgnoreCase) &&
                    resxFile.GetText(context.CancellationToken)?.ToString() is { Length: > 0 } content)
                {
                    var localeSource = GenerateLocaleClass(resxFile.Path, "default", ParseResxEntries(content));
                    context.AddSource("default.g.cs", SourceText.From(localeSource, Encoding.UTF8));
                    continue;
                }

                var fileName = Path.GetFileNameWithoutExtension(resxFile.Path);
                var localeName = fileName.Substring(fileName.IndexOf('.') + 1);

                var enumName = string.Join("", localeName.Split('-').Select(p => p.Length == 0 ? p : char.ToUpper(p[0]) + p.Substring(1)));
                localeNamesMap[enumName] = localeName;

                content = resxFile.GetText(context.CancellationToken)?.ToString();
                if (content is not { Length: > 0 }) continue;

                {
                    var localeSource = GenerateLocaleClass(resxFile.Path, localeName.Replace('-', '_'), ParseResxEntries(content));
                    context.AddSource($"{localeName}.g.cs", SourceText.From(localeSource, Encoding.UTF8));
                }
            }

            var localeManagerSource = GenerateLocaleManagerClass(defaultResxFile.Path, localeNamesMap);
            context.AddSource("LocaleManager.g.cs", SourceText.From(localeManagerSource, Encoding.UTF8));

            var localeNameTypeConverterSource = GenerateLocaleNameTypeConverterClass();
            context.AddSource("LocaleNameTypeConverter.g.cs", SourceText.From(localeNameTypeConverterSource, Encoding.UTF8));

            var localeNameSource = GenerateLocaleNameClass(defaultResxFile.Path, localeNamesMap);
            context.AddSource("LocaleName.g.cs", SourceText.From(localeNameSource, Encoding.UTF8));

            var localeNameExtensionSource = GenerateLocaleNameExtensionClass(defaultResxFile.Path, localeNamesMap);
            context.AddSource("LocaleNameExtensions.g.cs", SourceText.From(localeNameExtensionSource, Encoding.UTF8));
        }
        catch (Exception ex)
        {
            // Report diagnostic for any errors
            context.ReportDiagnostic(
                Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "I18N002",
                        "I18N Generation Error",
                        $"Error generating I18N code: {ex.Message}",
                        "I18N",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    Location.None));
        }
    }

    private static Dictionary<string, string> ParseResxEntries(string resxContent)
    {
        var entries = new Dictionary<string, string>();

        try
        {
            var doc = XDocument.Parse(resxContent);
            var dataNodes = doc.Root?.Elements("data");

            if (dataNodes != null)
            {
                foreach (var dataNode in dataNodes)
                {
                    var nameAttr = dataNode.Attribute("name");
                    var valueNode = dataNode.Element("value");

                    if (nameAttr != null && valueNode != null)
                    {
                        entries[nameAttr.Value] = valueNode.Value;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Silently fail and return an empty dictionary
            return new Dictionary<string, string>();
        }

        return entries;
    }

    private static string GenerateLocaleKeyClass(string resxPath, Dictionary<string, string> entries)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $$"""
              // Generated by Everywhere.I18N.SourceGenerator, do not edit manually
              // Edit {{Path.GetFileName(resxPath)}} instead, run the generator or build project to update this file

              #nullable enable

              namespace Everywhere.I18N;

              /// <summary>
              ///     Provides strongly-typed keys for localized strings.
              /// </summary>
              partial class LocaleKey
              {
              """);

        foreach (var entry in entries)
        {
            var escapedSummary = SecurityElement.Escape(entry.Value);
            if (escapedSummary is not null && escapedSummary.Contains('\n'))
            {
                sb.AppendLine("    /// <summary>");
                foreach (var summaryLine in escapedSummary.Split('\n'))
                {
                    sb.AppendLine($"    /// {summaryLine}");
                }
                sb.AppendLine("    /// </summary>");
            }
            else
            {
                sb.AppendLine($"    /// <summary>{escapedSummary}</summary>");
            }

            var escapedKey = EscapeVariableName(entry.Key);
            sb.AppendLine($"    public const string {escapedKey} = \"{entry.Key}\";");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string GenerateLocaleResolverClass(string resxPath, Dictionary<string, string> entries)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $$"""
              // Generated by Everywhere.I18N.SourceGenerator, do not edit manually
              // Edit {{Path.GetFileName(resxPath)}} instead, run the generator or build project to update this file

              #nullable enable

              namespace Everywhere.I18N;

              /// <summary>
              ///     Provides strongly-typed access to localized strings.
              /// </summary>
              partial class LocaleResolver
              {
                  [System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
                  [System.Diagnostics.DebuggerNonUserCode]
                  [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
                  private static string Resolve(string key)
                  {
                      return LocaleManager.Shared[key]?.ToString() ?? string.Empty;
                  }

              """);

        foreach (var entry in entries)
        {
            var escapedSummary = SecurityElement.Escape(entry.Value);
            if (escapedSummary is not null && escapedSummary.Contains('\n'))
            {
                sb.AppendLine("    /// <summary>");
                foreach (var summaryLine in escapedSummary.Split('\n'))
                {
                    sb.AppendLine($"    /// {summaryLine}");
                }
                sb.AppendLine("    /// </summary>");
            }
            else
            {
                sb.AppendLine($"    /// <summary>{escapedSummary}</summary>");
            }

            var escapedKey = EscapeVariableName(entry.Key);
            sb.AppendLine($"    public static string {escapedKey} => Resolve(\"{entry.Key}\");");
        }

        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string EscapeVariableName(string s)
    {
        // Replace invalid characters with underscores, and ensure it starts with a letter
        var escaped = new string(s.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        if (escaped.Length > 0 && char.IsDigit(escaped[0]))
        {
            escaped = "_" + escaped; // Ensure it starts with a letter or underscore
        }
        return escaped;
    }

    private static string GenerateLocaleClass(string resxPath, string escapedLocaleName, Dictionary<string, string> entries)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $$"""
              // Generated by Everywhere.I18N.SourceGenerator, do not edit manually
              // Edit {{Path.GetFileName(resxPath)}} instead, run the generator or build project to update this file

              #nullable enable

              namespace Everywhere.I18N;

              partial class LocaleManager
              {
              
              private class __{{escapedLocaleName}} : global::Avalonia.Controls.ResourceDictionary
              {
                  public __{{escapedLocaleName}}()
                  {
                      SetItems([
              """);

        foreach (var entry in entries)
        {
            var key = entry.Key;
            var value = entry.Value;

            // Escape quotes in the value
            value = value
                .Replace("\"", "\\\"")
#pragma warning disable RS1035 // Do not use APIs banned for analyzers
                .Replace(Environment.NewLine, "\\n")
#pragma warning restore RS1035 // Do not use APIs banned for analyzers
                .Replace("\r", "\\n")
                .Replace("\n", "\\n");

            sb.AppendLine($"            new KeyValuePair<object, object?>(\"{key}\", \"{value}\"),");
        }

        sb.AppendLine(
            """
                    ]);
                }
            }
            
            }
            """);

        return sb.ToString();
    }

    private static string GenerateLocaleManagerClass(string resxPath, Dictionary<string, string> localeNamesMap)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $$"""
              // Generated by Everywhere.I18N.SourceGenerator, do not edit manually
              // Edit {{Path.GetFileName(resxPath)}} instead, run the generator or build project to update this file

              #nullable enable

              using global::System.Diagnostics.CodeAnalysis;
              using global::System.Globalization;
              using global::System.Reflection;
              using global::Avalonia.Controls;
              using global::Avalonia.Styling;
              using global::CommunityToolkit.Mvvm.Messaging;

              namespace Everywhere.I18N;

              public partial class LocaleManager : ResourceDictionary
              {
                  public static partial LocaleManager Shared => shared ?? throw new InvalidOperationException("LocaleManager is not initialized.");

                  private static LocaleManager? shared;
                  
                  private static readonly Dictionary<LocaleName, ResourceDictionary> Locales;

                  static LocaleManager()
                  {
                      Locales = new Dictionary<LocaleName, ResourceDictionary>({{localeNamesMap.Count}})
                      {
              """);

        foreach (var kvp in localeNamesMap)
        {
            var escapedLocaleName = kvp.Value.Replace("-", "_");
            sb.AppendLine($"            {{ LocaleName.{kvp.Key}, new __{escapedLocaleName}() }},");
        }

        sb.AppendLine(
            """
                    };
                }
                
                public LocaleManager()
                {
                    if (shared is not null) throw new InvalidOperationException("LocaleManager is already initialized.");
                    shared = this;
                    
                    var cultureInfo = CultureInfo.CurrentUICulture;
                    LocaleName? currentLocale = null;
                    while (!string.IsNullOrEmpty(cultureInfo.Name))
                    {
                        if (Enum.TryParse<LocaleName>(cultureInfo.Name.Replace("-", ""), true, out var localeEnum))
                        {
                            currentLocale = localeEnum;
                            break;
                        }

                        cultureInfo = cultureInfo.Parent;
                    }
                    
                    CurrentLocale = currentLocale ?? default(LocaleName);
                }

                public static LocaleName CurrentLocale
                {
                    get => _currentLocale.GetValueOrDefault();
                    set
                    {
                        var dispatcher = Avalonia.Threading.Dispatcher.UIThread;
                    
                        if (dispatcher.CheckAccess())
                        {
                            SetField();
                            return;
                        }
                        
                        dispatcher.Invoke(SetField);
                        
                        void SetField()
                        {
                            if (_currentLocale == value) return;
                            
                            var oldLocale = _currentLocale;
                            if (!Locales.TryGetValue(value, out var newLocale))
                            {
                                (value, newLocale) = Locales.First();
                            }
                            
                            _currentLocale = value;
                            Shared.SetItems(newLocale);
                        
                            WeakReferenceMessenger.Default.Send(new LocaleChangedMessage(oldLocale, value));
                        }
                    }
                }

                private static LocaleName? _currentLocale;
            }
            """);

        return sb.ToString();
    }

    private static string GenerateLocaleNameTypeConverterClass()
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            """
            // Generated by Everywhere.I18N.SourceGenerator, do not edit manually

            #nullable enable

            using System;
            using System.ComponentModel;
            using System.Globalization;

            namespace Everywhere.I18N;

            public class LocaleNameTypeConverter : TypeConverter
            {
                public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
                {
                    return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
                }

                public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
                {
                    if (value is string strValue)
                    {
                        return Enum.TryParse<LocaleName>(strValue, true, out var locale) ? locale : default(LocaleName);
                    }

                    return base.ConvertFrom(context, culture, value);
                }
            }
            """);

        return sb.ToString();
    }

    private static string GenerateLocaleNameClass(string resxPath, Dictionary<string, string> localeNamesMap)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $$"""
              // Generated by Everywhere.I18N.SourceGenerator, do not edit manually
              // Edit {{Path.GetFileName(resxPath)}} instead, run the generator or build project to update this file

              #nullable enable

              namespace Everywhere.I18N;

              [global::System.ComponentModel.TypeConverter(typeof(LocaleNameTypeConverter))]
              public enum LocaleName
              {
              """);

        foreach (var kvp in localeNamesMap)
        {
            sb.AppendLine($"    [DynamicResourceKey(LocaleKey.LocaleName_{kvp.Key})]");
            sb.AppendLine($"    {kvp.Key},");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string GenerateLocaleNameExtensionClass(string resxPath, Dictionary<string, string> localeNamesMap)
    {
        var sb = new StringBuilder();

        sb.AppendLine(
            $$"""
              // Generated by Everywhere.I18N.SourceGenerator, do not edit manually
              // Edit {{Path.GetFileName(resxPath)}} instead, run the generator or build project to update this file

              #nullable enable

              namespace Everywhere.I18N;

              public static class LocaleNameExtensions
              {
                  /// <summary>
                  /// Converts the LocaleName enum to a human-readable display name in the respective language.
                  /// </summary>
                  public static string ToNativeName(this LocaleName localeName)
                  {
                      return localeName switch
                      {
              """);

        foreach (var kvp in localeNamesMap)
        {
            var nativeName = kvp.Value == "default" ? "English" : new CultureInfo(kvp.Value).NativeName;
            sb.AppendLine($"            LocaleName.{kvp.Key} => \"{nativeName}\",");
        }

        sb.AppendLine(
            """
                        _ => "English",
                    };
                }

            """);

        sb.AppendLine(
            """
                /// <summary>
                /// Converts the LocaleName enum to a human-readable display name in English.
                /// </summary>
                public static string ToEnglishName(this LocaleName localeName)
                {
                    return localeName switch
                    {
            """);

        foreach (var kvp in localeNamesMap)
        {
            var englishName = kvp.Value == "default" ? "English" : new CultureInfo(kvp.Value).EnglishName;
            sb.AppendLine($"            LocaleName.{kvp.Key} => \"{englishName}\",");
        }

        sb.AppendLine(
            """
                        _ => "English",
                    };
                }
            }
            """);

        return sb.ToString();
    }
}