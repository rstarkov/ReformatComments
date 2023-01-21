using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RT.Util;
using RT.Util.ExtensionMethods;

namespace RT.VisualStudio
{
    public static class CommentFormatter
    {
        private static string[] _inlineTags = new[] { "see", "paramref", "typeparamref", "c" };
        private static string[] _blockLevelTags = new[] { "code", "para", "list", "description" };

        private enum CommentType { None, CSharp, VB }

        public static string ReformatComments(string source)
        {
            var text = Regex.Split(source.TrimEnd(), @"\r?\n", RegexOptions.Singleline);
            var commentTypes = text
                .Select(line => new { Line = line, Match = Regex.Match(line, @"^\s*(///|''')(.*)$") })
                .Select(inf => new
                {
                    Line = inf.Line,
                    Type = inf.Match.Success ? inf.Match.Groups[1].Value == "///" ? CommentType.CSharp : CommentType.VB : CommentType.None,
                    Rest = inf.Match.Success ? inf.Match.Groups[2].Value : null
                })
                .ToArray();
            var result = new StringBuilder();

            foreach (var gr in commentTypes.GroupConsecutiveBy(inf => inf.Type))
            {
                if (gr.Key == CommentType.None)
                {
                    foreach (var inf in gr)
                        result.AppendLine(inf.Line);
                    continue;
                }

                try
                {
                    var indentationLength = Regex.Match(text[gr.Index], @"^\s*", RegexOptions.Multiline).Length;
                    var comment = XElement.Parse(
                        "<outer>{0}</outer>".Fmt(gr.Select(inf => inf.Rest).JoinString(Environment.NewLine)),
                        LoadOptions.PreserveWhitespace
                    );

                    var wantedWidth = 126;
                    var indentation = new string(' ', indentationLength) + (
                        gr.Key == CommentType.CSharp ? "/// " :
                        gr.Key == CommentType.VB ? "''' " : throw new InvalidOperationException());

                    // Special case: single <summary> tag that fits on a line
                    if (comment.Elements().Count() == 1 && comment.Elements().First().Name.LocalName == "summary" && !comment.Elements().First().Attributes().Any())
                    {
                        var summary = reformatComment(comment.Elements().First().Nodes(), false);
                        if (indentation.Length + "<summary>".Length + summary.Length <= wantedWidth)
                        {
                            result.Append(indentation);
                            result.Append("<summary>");
                            result.Append(summary);
                            result.AppendLine("</summary>");
                            continue;
                        }
                    }

                    var lines = reformatComment(comment.Nodes(), true).Trim().Split(Environment.NewLine);
                    var inCode = false;
                    foreach (var lineF in lines)
                    {
                        var line = lineF;

                        if (line.Contains("<code"))
                            inCode = true;

                        if (inCode)
                        {
                            // Append the code without word-wrapping
                            result.Append(indentation);
                            result.AppendLine(line);
                            if (line.Contains("</code>"))
                                inCode = false;
                        }
                        else
                        {
                            // Append the lines with word-wrapping, while excluding the end tags from the wrap width
                            var endtags = Regex.Match(line, @"(</\w+>)+$");
                            if (endtags.Success)
                                line = line.Substring(0, endtags.Index);
                            var wrappedLines = line.WordWrap(wantedWidth - indentation.Length).ToList();
                            if (endtags.Success)
                                wrappedLines[wrappedLines.Count - 1] += endtags.Value;
                            foreach (var wrappedLine in wrappedLines)
                            {
                                result.Append(indentation);
                                result.AppendLine(wrappedLine);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    result.AppendLine("The following comment is not valid: {0}".Fmt(e.Message, e.GetType().Name));
                    foreach (var line in text.Skip(gr.Index).Take(gr.Count))
                        result.AppendLine(line);
                }
            }

            return result.ToString();
        }

        private static string reformatComment(IEnumerable<XNode> nodes, bool topLevel, bool keepIndentation = false)
        {
            var sb = new StringBuilder();

            Func<XNode, string[], bool> isIn = (XNode node, string[] names) => names.Contains(((XElement) node).Name.LocalName);

            if (topLevel || nodes.All(n => (n is XText && string.IsNullOrWhiteSpace(((XText) n).Value)) || (n is XElement && !isIn(n, _inlineTags))) && nodes.Any(n => n is XElement && isIn(n, _blockLevelTags)))
            {
                // All tags are block-level or unknown ⇒ use block-level logic, which means:
                // • Discard all the whitespace between tags
                // • Put each opening tag on a new line and indent its contents
                var first = true;
                foreach (var elem in nodes.OfType<XElement>())
                {
                    if (!first)
                        sb.AppendLine();
                    first = false;
                    sb.Append(formatTag(elem, true, () =>
                    {
                        if (elem.Name.LocalName != "list")
                            return reformatComment(elem.Nodes(), false, keepIndentation: elem.Name.LocalName == "code").Indent(4);

                        // Handle <list> tags specially so that <item> and <description> don’t cause double-indentation
                        if (!elem.Nodes().All(n => n is XText || (n is XElement && ((XElement) n).Name.LocalName == "item")))
                            throw new InvalidOperationException("A “list” tag is not supposed to contain anything other than “item” tags.");
                        return elem.Nodes().OfType<XElement>().Select(e => "<item>{0}</item>".Fmt(reformatComment(e.Nodes(), false))).JoinString(Environment.NewLine).Indent(4);
                    }));
                }
                return sb.ToString();
            }
            else if (nodes.All(n => n is XText || (n is XElement && !isIn(n, _blockLevelTags))) && (!nodes.OfType<XElement>().Any() || nodes.OfType<XElement>().Any(n => isIn(n, _inlineTags))))
            {
                var first = true;
                string lastToAdd = null;

                // All nodes are text, inline-level or unknown ⇒ use inline-level logic, which means:
                // • Remove all single newlines (but keep double-newlines)
                // • Put all the nodes inline with the text
                foreach (var node in nodes)
                {
                    var text = node as XText;
                    var element = node as XElement;
                    if (text != null)
                    {
                        var value = !first ? text.Value : keepIndentation ? text.Value.TrimStart('\r', '\n') : text.Value.TrimStart();
                        sb.Append(lastToAdd);
                        if (keepIndentation)
                            lastToAdd = value.HtmlEscape(leaveSingleQuotesAlone: true, leaveDoubleQuotesAlone: true);
                        else
                            // Replace all “lone” newlines with spaces
                            lastToAdd = Regex.Replace(value, @"(?<!\n) *\r?\n *(?!\r?\n)", " ").HtmlEscape(leaveSingleQuotesAlone: true, leaveDoubleQuotesAlone: true);
                    }
                    else
                    {
                        sb.Append(lastToAdd);
                        lastToAdd = formatTag(element, false, () => reformatComment(element.Nodes(), false));
                    }
                    first = false;
                }
                sb.Append(lastToAdd.TrimEnd());

                return setIndentation(sb.ToString(), 0);
            }
            else
            {
                var firstBlock = (XElement) nodes.FirstOrDefault(n => n is XElement && isIn(n, _blockLevelTags));
                var firstInline = (XElement) nodes.FirstOrDefault(n => n is XElement && isIn(n, _inlineTags));
                if (firstBlock != null && firstInline != null)
                    throw new InvalidOperationException("“{0}” is block-level, but “{1}” is inline-level.".Fmt(firstBlock.Name.LocalName, firstInline.Name.LocalName));
                var firstUnknown = (XElement) nodes.FirstOrDefault(n => n is XElement && !isIn(n, _blockLevelTags) && !isIn(n, _inlineTags));
                if (firstUnknown != null)
                    throw new InvalidOperationException("I don’t know whether “{0}” is inline-level or block-level.".Fmt(firstUnknown.Name.LocalName));
                else
                    throw new InvalidOperationException("This comment contains an element that contains both block-level elements as well as raw text. Wrap the text in <para>.");
            }
        }

        private static string setIndentation(string text, int indent)
        {
            var lines = text.Split("\r\n", "\r", "\n");
            int minIndent = int.MaxValue;
            foreach (var line in lines)
            {
                int pos = 0;
                while (pos < minIndent)
                {
                    if (pos == line.Length)
                    {
                        pos = int.MaxValue;
                        break;
                    }
                    if (line[pos] != ' ')
                        break;
                    pos++;
                }
                if (minIndent > pos)
                    minIndent = pos;
            }

            var result = new StringBuilder();
            var indentStr = new string(' ', indent);
            foreach (var line in lines)
            {
                if (line.Trim() == "")
                    result.AppendLine();
                else
                    result.AppendLine(indentStr + line.Substring(minIndent));
            }
            return result.ToString().Trim('\r', '\n');
        }

        private static string formatTag(XElement elem, bool blockLevel, Func<string> inside)
        {
            if (!elem.Nodes().Any())
                // Self-closing tag
                return "<{0}{1}/>".Fmt(elem.Name.LocalName, elem.Attributes().Select(attr => @" {0}=""{1}""".Fmt(attr.Name.LocalName, attr.Value.HtmlEscape())).JoinString());

            return "<{0}{1}>{2}{3}</{0}>".Fmt(elem.Name.LocalName, elem.Attributes().Select(attr => @" {0}=""{1}""".Fmt(attr.Name.LocalName, attr.Value.HtmlEscape())).JoinString(), blockLevel ? Environment.NewLine : null, inside());
        }
    }
}
