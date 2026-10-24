using System.Globalization;
using System.Text;
using System.Xml;

namespace PcDiag.Engine.Events;

/// <summary>
/// Builds the structured XML query (QueryList) for one channel, so Windows filters events before they reach the
/// Agent. Each rule becomes its own Select with a few expressions; Windows requires a structured query for more than
/// 20 expressions, and small selectors stay well clear of that. Ignore rules become Suppress elements. Field
/// conditions are not expressible in the Event Log's XPath subset and are checked by the classifier.
/// </summary>
public static class EventQueryBuilder
{
    /// <summary>Event IDs per Select element.</summary>
    internal const int MaxIdsPerSelect = 12;

    /// <summary>
    /// Builds the query for <paramref name="channel"/>. With <paramref name="within"/>, only events from that far
    /// back are selected (for backfill). Returns null when no rule selects from the channel.
    /// </summary>
    /// <param name="rules">The active rules.</param>
    /// <param name="channel">Channel name.</param>
    /// <param name="within">Only events newer than this (uses the Event Log's timediff function).</param>
    /// <param name="sinceBootRulesOnly">Only rules marked sinceBoot.</param>
    public static string? Build(EventRuleSet rules, string channel, TimeSpan? within = null, bool sinceBootRulesOnly = false)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var onChannel = rules.Rules.Where(r => r.Rule.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase)).ToList();
        var selects = onChannel
            .Where(r => !r.Rule.Ignore && (!sinceBootRulesOnly || r.Rule.SinceBoot))
            .SelectMany(r => Expressions(r.Rule, within))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (selects.Count == 0)
        {
            return null;
        }

        var suppresses = onChannel.Where(r => r.Rule.Ignore).SelectMany(r => Expressions(r.Rule, null)).Distinct(StringComparer.Ordinal);

        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false };
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, settings))
        {
            writer.WriteStartElement("QueryList");
            writer.WriteStartElement("Query");
            writer.WriteAttributeString("Id", "0");
            writer.WriteAttributeString("Path", channel);
            foreach (var select in selects)
            {
                writer.WriteStartElement("Select");
                writer.WriteAttributeString("Path", channel);
                writer.WriteString(select);
                writer.WriteEndElement();
            }

            foreach (var suppress in suppresses)
            {
                writer.WriteStartElement("Suppress");
                writer.WriteAttributeString("Path", channel);
                writer.WriteString(suppress);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return builder.ToString();
    }

    /// <summary>XPath selectors for one rule (more than one when it lists many event IDs).</summary>
    internal static IEnumerable<string> Expressions(EventRule rule, TimeSpan? within)
    {
        var common = new List<string>();
        if (rule.Providers.Count > 0)
        {
            // Provider names are validated to exclude quotes, so single-quoted literals are safe.
            common.Add(Group(rule.Providers.Distinct(StringComparer.OrdinalIgnoreCase).Select(p => $"Provider[@Name='{p}']")));
        }

        if (rule.Levels.Count > 0)
        {
            common.Add(Group(rule.Levels.Distinct().Order().Select(l => "Level=" + l.ToString(CultureInfo.InvariantCulture))));
        }

        if (within is { } span)
        {
            var ms = Math.Max(1L, (long)span.TotalMilliseconds);
            common.Add("TimeCreated[timediff(@SystemTime) <= " + ms.ToString(CultureInfo.InvariantCulture) + "]");
        }

        var ids = rule.EventIds.Distinct().Order().ToList();
        if (ids.Count == 0)
        {
            yield return Wrap(common);
            yield break;
        }

        foreach (var chunk in ids.Chunk(MaxIdsPerSelect))
        {
            yield return Wrap([.. common, Group(chunk.Select(id => "EventID=" + id.ToString(CultureInfo.InvariantCulture)))]);
        }
    }

    private static string Group(IEnumerable<string> alternatives)
    {
        var list = alternatives.ToList();
        return list.Count == 1 ? list[0] : "(" + string.Join(" or ", list) + ")";
    }

    private static string Wrap(List<string> conditions) =>
        conditions.Count == 0 ? "*" : "*[System[" + string.Join(" and ", conditions) + "]]";
}
