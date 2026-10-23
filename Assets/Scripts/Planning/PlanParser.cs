using System;
using System.Collections.Generic;
using System.Text;
using Game.Actions;

namespace Game.Planning
{
    // Pure text -> canonical plan JSON. Output is strings only: no Execution types.
    // Alias collisions (e.g. "ataca" matching AttackSword vs Punch) resolve to the
    // FIRST manifest entry; weapon-aware selection (IsAvailable hasWeapon) is the
    // executor's job via ResolveFallback, not the parser's.
    // DECISION: > MaxPlanSize requested actions is an ERROR (not truncation), so the
    // player sees the limit instead of silently losing tail actions.
    public sealed class PlanParser
    {
        private static readonly string[] Separators =
        {
            ",", "\n", ";", " y ", " then ", " despu\u00e9s ", " despues ",
            " luego ", " e "
        };

        public PlanParseResult Parse(string input, ActionManifest manifest)
        {
            var result = new PlanParseResult();
            if (manifest == null)
            {
                result.Errors.Add("missing action manifest");
                return result;
            }
            if (string.IsNullOrWhiteSpace(input))
            {
                result.Errors.Add("empty command");
                return result;
            }

            var tokens = SplitOnAliasBoundaries(Tokenize(input), BuildAliasPhrases(manifest));
            if (tokens.Count == 0)
            {
                result.Errors.Add("empty command");
                return result;
            }
            if (tokens.Count > manifest.MaxPlanSize)
            {
                result.Errors.Add(string.Format(
                    "too many actions ({0}), max is {1}", tokens.Count, manifest.MaxPlanSize));
                return result;
            }

            var actions = new List<ParsedAction>();
            foreach (var token in tokens)
            {
                string direction = ExtractDirection(token);
                string verb = StripDirectionPhrases(token).Trim();
                // Whole token first: some aliases embed a direction ("bloquea izquierda" -> BlockLeft).
                var def = manifest.FindByAlias(token);
                if (def == null) def = manifest.FindByAlias(verb);
                if (def == null) def = MatchSingleWord(manifest, verb);
                if (def == null) def = MatchPartial(manifest, verb, result);
                if (def == null)
                {
                    def = manifest.Find("BlockCenter");
                    result.Warnings.Add(string.Format(
                        "unrecognized command '{0}', using defensive action", token.Trim()));
                    result.AppliedFallbacks.Add(token.Trim() + "->BlockCenter");
                }
                if (def == null)
                {
                    result.Errors.Add("manifest lacks BlockCenter fallback");
                    return result;
                }
                var pa = new ParsedAction { Type = def.ActionId };
                if (def.NeedsTarget && !string.IsNullOrEmpty(direction))
                    pa.Target = direction;
                actions.Add(pa);
            }

            result.Success = true;
            result.PlanJson = BuildJson(actions);
            return result;
        }

        private static List<string> Tokenize(string input)
        {
            var lowered = " " + input.ToLowerInvariant() + " ";
            foreach (var sep in Separators)
                lowered = lowered.Replace(sep, "\n");
            var tokens = new List<string>();
            foreach (var part in lowered.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = part.Trim();
                if (!string.IsNullOrEmpty(t)) tokens.Add(t);
            }
            return tokens;
        }

        // An alias right after one of these is the complement of the previous verb
        // ("lanza la espada", "throw the sword"), not the start of a new action.
        private static readonly HashSet<string> ComplementMarkers = new HashSet<string>
        {
            "a", "al", "el", "la", "lo", "los", "las", "un", "una", "de", "del", "con",
            "mi", "tu", "su", "sus", "the", "an", "with", "to", "at", "my", "your", "his", "her"
        };

        private static readonly char[] WordPunctuation = { ',', '.', '!', '?', ';', ':', '\u00a1', '\u00bf' };

        // Manifest ids + aliases as word sequences, longest first for greedy matching.
        private static List<string[]> BuildAliasPhrases(ActionManifest manifest)
        {
            var seen = new HashSet<string>();
            var phrases = new List<string[]>();
            foreach (var def in manifest.Actions)
            {
                if (def == null) continue;
                AddAliasPhrase(def.ActionId, seen, phrases);
                foreach (var alias in def.Aliases)
                    AddAliasPhrase(alias, seen, phrases);
            }
            phrases.Sort((a, b) => b.Length.CompareTo(a.Length));
            return phrases;
        }

        private static void AddAliasPhrase(string alias, HashSet<string> seen, List<string[]> phrases)
        {
            if (string.IsNullOrWhiteSpace(alias)) return;
            var words = SplitWords(alias.ToLowerInvariant());
            if (words.Length > 0 && seen.Add(string.Join(" ", words)))
                phrases.Add(words);
        }

        // Splits space-separated chunks ("corre golpe esquiva") at each alias start. Words
        // that aren't aliases (direction phrases, fillers) stay with the preceding action, so
        // "ataca al de la derecha" remains one action. Chunks without aliases are untouched.
        private static List<string> SplitOnAliasBoundaries(List<string> chunks, List<string[]> aliasPhrases)
        {
            var result = new List<string>();
            foreach (var chunk in chunks)
            {
                var words = SplitWords(chunk);
                var current = new List<string>();
                bool currentHasAction = false;
                int i = 0;
                while (i < words.Length)
                {
                    int matched = LongestAliasAt(words, i, aliasPhrases);
                    if (matched == 0)
                    {
                        current.Add(words[i]);
                        i++;
                        continue;
                    }

                    bool isComplement = i > 0 && ComplementMarkers.Contains(CleanWord(words[i - 1]));
                    if (currentHasAction && !isComplement)
                    {
                        result.Add(string.Join(" ", current));
                        current.Clear();
                    }
                    currentHasAction = true;
                    for (int k = 0; k < matched; k++)
                        current.Add(words[i + k]);
                    i += matched;
                }
                if (current.Count > 0)
                    result.Add(string.Join(" ", current));
            }
            return result;
        }

        private static int LongestAliasAt(string[] words, int start, List<string[]> aliasPhrases)
        {
            foreach (var phrase in aliasPhrases)
            {
                if (start + phrase.Length > words.Length) continue;
                bool match = true;
                for (int k = 0; k < phrase.Length && match; k++)
                    match = CleanWord(words[start + k]) == phrase[k];
                if (match) return phrase.Length;
            }
            return 0;
        }

        private static string[] SplitWords(string text)
        {
            return text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string CleanWord(string word)
        {
            return word.Trim(WordPunctuation);
        }

        private static string ExtractDirection(string token)
        {
            string t = token;
            if (ContainsAny(t, " a la derecha", "al de la derecha", "de la derecha", "right", "derecha"))
                return "right";
            if (ContainsAny(t, " a la izquierda", "al de la izquierda", "de la izquierda", "left", "izquierda"))
                return "left";
            if (ContainsAny(t, "al del centro", "del centro", "center", "centro", "middle", "medio", "en medio"))
                return "center";
            return null;
        }

        private static string StripDirectionPhrases(string token)
        {
            string[] phrases =
            {
                "al de la derecha", "a la derecha", "de la derecha",
                "al de la izquierda", "a la izquierda", "de la izquierda",
                "al del centro", "del centro", "en el centro",
                "right", "left", "center", "middle",
                "derecha", "izquierda", "centro", "medio",
                "al de", "a la", "a el", "al ", "del ", "de la ", "la espada", "la "
            };
            string t = " " + token + " ";
            foreach (var p in phrases)
                t = t.Replace(" " + p + " ", " ").Replace(" " + p, " ");
            return t;
        }

        private static bool ContainsAny(string haystack, params string[] needles)
        {
            foreach (var n in needles)
                if (haystack.Contains(n)) return true;
            return false;
        }

        private static ActionDefinition MatchSingleWord(ActionManifest manifest, string verb)
        {
            foreach (var word in verb.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var def = manifest.FindByAlias(word.Trim(',', '.', '!', '?'));
                if (def != null) return def;
            }
            return null;
        }

        private static ActionDefinition MatchPartial(ActionManifest manifest, string verb, PlanParseResult result)
        {
            foreach (var def in manifest.Actions)
            {
                if (def == null) continue;
                if (verb.Contains(def.ActionId.ToLowerInvariant()))
                {
                    result.Warnings.Add(string.Format(
                        "partial match '{0}' -> {1}", verb, def.ActionId));
                    return def;
                }
                if (def.Aliases != null)
                {
                    foreach (var alias in def.Aliases)
                    {
                        if (!string.IsNullOrWhiteSpace(alias)
                            && verb.Contains(alias.Trim().ToLowerInvariant()))
                        {
                            result.Warnings.Add(string.Format(
                                "partial match '{0}' -> {1}", verb, def.ActionId));
                            return def;
                        }
                    }
                }
            }
            return null;
        }

        private static string BuildJson(List<ParsedAction> actions)
        {
            var sb = new StringBuilder("{\"actions\":[");
            for (int i = 0; i < actions.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"type\":\"").Append(actions[i].Type).Append('\"');
                if (!string.IsNullOrEmpty(actions[i].Target))
                    sb.Append(",\"target\":\"").Append(actions[i].Target).Append('\"');
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }
    }

    public sealed class ParsedAction
    {
        public string Type;
        public string Target;
    }

    public sealed class PlanParseResult
    {
        public bool Success;
        public string PlanJson;
        public readonly List<string> Errors = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> AppliedFallbacks = new List<string>();
    }
}
