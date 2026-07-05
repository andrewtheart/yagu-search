namespace Yagu.Models;

public enum GroupMode
{
    None,
    Folder,
    DateRangeModifiedCreated,
    DateRange = DateRangeModifiedCreated,
    Extension,
    FileSize,
    DateRangeModified,
    DateRangeCreated,
    DateToday,
    DateYesterday,
    DateThisWeek,
    DateThisMonth,
    DateThisYear,
    DatePast2Years,
    DatePast5Years,
    DatePast10Years,
    DatePast20Years,
    DatePast30Years,
    DatePast50Years,
}

public enum DateRangeFilter
{
    None,
    PastDay,
    PastWeek,
    PastTwoWeeks,
    PastMonth,
    PastThreeMonths,
    PastSixMonths,
    PastNineMonths,
    PastYear,
    PastTwoYears,
    PastThreeYears,
    PastFiveYears,
}
