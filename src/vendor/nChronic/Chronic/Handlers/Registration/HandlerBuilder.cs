using System;
using System.Collections.Generic;

namespace Chronic.Handlers
{
    public class Repetition
    {
        private readonly HandlerBuilder _builder;
        private readonly HandlerBuilder _innerBuilder;

        public Repetition(HandlerBuilder builder, HandlerBuilder innerBuilder)
        {
            _builder = builder;
            _innerBuilder = innerBuilder;
        }

        public HandlerBuilder AnyNumberOfTimes()
        {
            var pattern = new RepeatPattern(_innerBuilder._patternParts, RepeatPattern.Inifinite);
            _builder._patternParts.Add(pattern);
            return _builder;
        }

        public HandlerBuilder Times(int number)
        {
            var pattern = new RepeatPattern(_innerBuilder._patternParts, number);
            _builder._patternParts.Add(pattern);
            return _builder;
        }
    }

    public class HandlerBuilder
    {
        internal readonly IList<HandlerPattern> _patternParts =
            new List<HandlerPattern>();

        public IEnumerable<HandlerPattern> GetPatterns()
        {
            return new List<HandlerPattern>(_patternParts);
        }

        public Type BaseHandler { get; private set; }

        // De-dynamic'd for Native AOT: the implicit operator below used Activator.CreateInstance on
        // this Type (reflection), which AOT can't do over a possibly-trimmed ctor. Capture a strongly
        // typed factory at registration time instead — `new THandler()` is AOT-safe.
        private Func<IHandler> _baseHandlerFactory;

        public HandlerBuilder Using<THandler>() where THandler : class, IHandler, new()
        {
            BaseHandler = typeof(THandler);
            _baseHandlerFactory = static () => new THandler();
            return this;
        }

        public HandlerBuilder UsingNothing()
        {
            BaseHandler = null;
            _baseHandlerFactory = null;
            return this;
        }

        public HandlerBuilder Optional<THandler>()
        {
            _patternParts.Add(new TagPattern(typeof(THandler), true));
            return this;
        }

        public HandlerBuilder Required<THandler>()
        {
            _patternParts.Add(new TagPattern(typeof(THandler), false));
            return this;
        }

        public Repetition Repeat(Action<HandlerBuilder> pattern)
        {
            if (pattern == null) throw new ArgumentNullException("pattern");

            var builder = new HandlerBuilder();
            pattern(builder);
            return new Repetition(this, builder);
        }

        public HandlerBuilder Required(HandlerType type)
        {
            _patternParts.Add(new HandlerTypePattern(type, false));
            return this;
        }

        public HandlerBuilder Optional(HandlerType type)
        {
            _patternParts.Add(new HandlerTypePattern(type, true));
            return this;
        }

        public static implicit operator ComplexHandler(HandlerBuilder builder)
        {
            return new ComplexHandler(
                builder._baseHandlerFactory != null
                    ? builder._baseHandlerFactory()
                    : null,
                builder._patternParts);
        }
    }
}