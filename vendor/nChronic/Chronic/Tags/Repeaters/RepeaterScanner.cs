using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Chronic.Tags.Repeaters;

namespace Chronic.Tags.Repeaters
{
    public class RepeaterScanner : ITokenScanner
    {
        static readonly List<Func<Token, Options, ITag>> _scanners = new List
            <Func<Token, Options, ITag>>
            {
                //ScanSeasonNames(token, options),
                ScanMonthNames,
                ScanDayNames,
                ScanDayPortions,
                ScanTimes,
                ScanUnits,
            };

        public IList<Token> Scan(IList<Token> tokens, Options options)
        {
            tokens.ForEach(token =>
                {
                    foreach (var scanner in _scanners)
                    {
                        var tag = scanner(token, options);
                        if (tag != null)
                        {
                            token.Tag(tag);
                            break;
                        }
                    }
                });

            return tokens;
        }

        static ITag ScanUnits(Token token, Options options)
        {
            ITag tag = null;
            foreach (var item in UnitPatterns)
            {
                if (item.Pattern.IsMatch(token.Value))
                {
                    tag = item.Factory(options);
                }
            }
            return tag;
        }

        static ITag ScanTimes(Token token, Options options)
        {
            var match = _timePattern.Match(token.Value);
            if (match.Success)
            {
                return new RepeaterTime(match.Value);
            }
            return null;
        }

        static ITag ScanDayPortions(Token token, Options options)
        {
            ITag tag = null;
            foreach (var item in DayPortionPatterns)
            {
                if (item.Pattern.IsMatch(token.Value))
                {
                    tag = new EnumRepeaterDayPortion(item.Portion);
                }
            }
            return tag;
        }

        static ITag ScanDayNames(Token token, Options options)
        {
            ITag tag = null;
            foreach (var item in DayPatterns)
            {
                if (item.Pattern.IsMatch(token.Value))
                {
                    tag = new RepeaterDayName(item.Day);
                }
            }
            return tag;
        }

        static ITag ScanMonthNames(Token token, Options options)
        {
            ITag tag = null;
            foreach (var item in MonthPatterns)
            {
                if (item.Pattern.IsMatch(token.Value))
                {
                    tag = new RepeaterMonthName(item.Month);
                }
            }
            return tag;
        }

        static ITag ScanSeasonNames(Token token, Options options)
        {
            throw new NotImplementedException();
        }

        static readonly Regex _timePattern =
            @"^\d{1,2}(:?\d{2})?([\.:]?\d{2})?$".Compile();

        // De-dynamic'd for Native AOT: these tables were List<dynamic> of anonymous objects, whose
        // member access (item.Pattern/.Portion/.Day/.Month/.Unit) dispatched through the DLR. Concrete
        // record structs make the access static. UnitPattern additionally replaces the old
        // typeof(RepeaterX)+Activator.CreateInstance reflection with an explicit factory.
        private readonly record struct DayPortionPattern(Regex Pattern, DayPortion Portion);
        private readonly record struct DayPattern(Regex Pattern, DayOfWeek Day);
        private readonly record struct MonthPattern(Regex Pattern, MonthName Month);
        private readonly record struct UnitPattern(Regex Pattern, Func<Options, ITag> Factory);

        static readonly DayPortionPattern[] DayPortionPatterns = new DayPortionPattern[]
            {
                new DayPortionPattern("^ams?$".Compile(), DayPortion.AM),
                new DayPortionPattern("^pms?$".Compile(), DayPortion.PM),
                new DayPortionPattern("^mornings?$".Compile(), DayPortion.MORNING),
                new DayPortionPattern("^afternoons?$".Compile(), DayPortion.AFTERNOON),
                new DayPortionPattern("^evenings?$".Compile(), DayPortion.EVENING),
                new DayPortionPattern("^(night|nite)s?$".Compile(), DayPortion.NIGHT),
            };

        static readonly DayPattern[] DayPatterns = new DayPattern[]
            {
                new DayPattern("^m[ou]n(day)?$".Compile(), DayOfWeek.Monday),
                new DayPattern("^t(ue|eu|oo|u|)s(day)?$".Compile(), DayOfWeek.Tuesday),
                new DayPattern("^tue$".Compile(), DayOfWeek.Tuesday),
                new DayPattern("^we(dnes|nds|nns)day$".Compile(), DayOfWeek.Wednesday),
                new DayPattern("^wed$".Compile(), DayOfWeek.Wednesday),
                new DayPattern("^th(urs|ers)day$".Compile(), DayOfWeek.Thursday),
                new DayPattern("^thu$".Compile(), DayOfWeek.Thursday),
                new DayPattern("^fr[iy](day)?$".Compile(), DayOfWeek.Friday),
                new DayPattern("^sat(t?[ue]rday)?$".Compile(), DayOfWeek.Saturday),
                new DayPattern("^su[nm](day)?$".Compile(), DayOfWeek.Sunday),
            };

        static readonly MonthPattern[] MonthPatterns = new MonthPattern[]
            {
                new MonthPattern("^jan\\.?(uary)?$".Compile(), MonthName.January),
                new MonthPattern("^feb\\.?(ruary)?$".Compile(), MonthName.February),
                new MonthPattern("^mar\\.?(ch)?$".Compile(), MonthName.March),
                new MonthPattern("^apr\\.?(il)?$".Compile(), MonthName.April),
                new MonthPattern("^may$".Compile(), MonthName.May),
                new MonthPattern("^jun\\.?e?$".Compile(), MonthName.June),
                new MonthPattern("^jul\\.?y?$".Compile(), MonthName.July),
                new MonthPattern("^aug\\.?(ust)?$".Compile(), MonthName.August),
                new MonthPattern("^sep\\.?(t\\.?|tember)?$".Compile(), MonthName.September),
                new MonthPattern("^oct\\.?(ober)?$".Compile(), MonthName.October),
                new MonthPattern("^nov\\.?(ember)?$".Compile(), MonthName.November),
                new MonthPattern("^dec\\.?(ember)?$".Compile(), MonthName.December),
            };

        static readonly UnitPattern[] UnitPatterns = new UnitPattern[]
            {
                new UnitPattern("^years?$".Compile(), _ => new RepeaterYear()),
                new UnitPattern("^seasons?$".Compile(), _ => new RepeaterSeason()),
                new UnitPattern("^months?$".Compile(), _ => new RepeaterMonth()),
                new UnitPattern("^fortnights?$".Compile(), _ => new RepeaterFortnight()),
                new UnitPattern("^weeks?$".Compile(), o => new RepeaterWeek(o)),
                new UnitPattern("^weekends?$".Compile(), _ => new RepeaterWeekend()),
                new UnitPattern("^days?$".Compile(), _ => new RepeaterDay()),
                new UnitPattern("^hours?$".Compile(), _ => new RepeaterHour()),
                new UnitPattern("^minutes?$".Compile(), _ => new RepeaterMinute()),
                new UnitPattern("^seconds?$".Compile(), _ => new RepeaterSecond())
            };
    }
}