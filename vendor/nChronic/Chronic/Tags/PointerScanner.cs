using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Chronic
{
    public class PointerScanner : ITokenScanner
    {
        // De-dynamic'd for Native AOT (see GrabberScanner): concrete struct instead of dynamic[].
        private readonly record struct PointerPattern(Regex Pattern, ITag Tag);

        static readonly PointerPattern[] Patterns = new PointerPattern[]
            {
                new PointerPattern(new Regex(@"\bin\b"), new Pointer(Pointer.Type.Future)),
                new PointerPattern(new Regex(@"\bfuture\b"), new Pointer(Pointer.Type.Future)),
                new PointerPattern(new Regex(@"\bpast\b"), new Pointer(Pointer.Type.Past)),
            };

        public IList<Token> Scan(IList<Token> tokens, Options options)
        {
            tokens.ForEach(ApplyTags);
            return tokens;
        }

        private void ApplyTags(Token token)
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