using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Chronic
{
    public class SeparatorScanner : ITokenScanner
    {
        // De-dynamic'd for Native AOT (see GrabberScanner): concrete struct instead of dynamic[].
        private readonly record struct SeparatorPattern(Regex Pattern, ITag Tag);

        static readonly SeparatorPattern[] Patterns = new SeparatorPattern[]
            {
                new SeparatorPattern(@"^,$".Compile(), new SeparatorComma()),
                new SeparatorPattern(@"^and$".Compile(), new SeparatorComma()),
                new SeparatorPattern(@"^(at|@)$".Compile(), new SeparatorAt()),
                new SeparatorPattern(@"^in$".Compile(), new SeparatorIn()),
                new SeparatorPattern(@"^/$".Compile(), new SeparatorDate(Separator.Type.Slash)),
                new SeparatorPattern(@"^-$".Compile(), new SeparatorDate(Separator.Type.Dash)),
                new SeparatorPattern(@"^on$".Compile(), new SeparatorOn()),
            };

        public IList<Token> Scan(IList<Token> tokens, Options options)
        {
            tokens.ForEach(ApplyTags);
            return tokens;
        }

        static void ApplyTags(Token token)
        {
            foreach (var pattern in Patterns)
            {
                if (pattern.Pattern.IsMatch(token.Value))
                {
                    token.Tag(pattern.Tag);
                }
            }
        }
    }
}