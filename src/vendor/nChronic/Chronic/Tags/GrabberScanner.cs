using System.Collections.Generic;

namespace Chronic
{
    public class GrabberScanner : ITokenScanner
    {
        // De-dynamic'd for Native AOT: anonymous objects on a dynamic[] dispatched member access
        // (match.Pattern/.Tag) through the DLR, which AOT does not support. A concrete struct makes
        // the access static.
        private readonly record struct GrabberMatch(string Pattern, ITag Tag);

        static readonly GrabberMatch[] _matches = new GrabberMatch[]
            {
                new GrabberMatch("last", new Grabber(Grabber.Type.Last)),
                new GrabberMatch("next", new Grabber(Grabber.Type.Next)),
                new GrabberMatch("this", new Grabber(Grabber.Type.This))
            };

        public IList<Token> Scan(IList<Token> tokens, Options options)
        {
            tokens.ForEach(ApplyGrabberTags);
            return tokens;
        }

        static void ApplyGrabberTags(Token token)
        {
            foreach (var match in _matches)
            {
                if (match.Pattern == token.Value)
                {
                    token.Tag(match.Tag);
                }
            }
        }
    }
}