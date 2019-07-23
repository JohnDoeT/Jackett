namespace Jackett.Common.Models
{
    public class CategoryMapping
    {
        public CategoryMapping(string trackerCat, string trackerCatDesc, int newzCat, string selectorId = null)
        {
            TrackerCategory = trackerCat;
            TrackerCategoryDesc = trackerCatDesc;
            NewzNabCategory = newzCat;
            SelectorId = selectorId;
        }

        public string TrackerCategory { get; private set; }
        public string TrackerCategoryDesc { get; private set; }
        public int NewzNabCategory { get; private set; }

        public string SelectorId { get; private set; }
    }
}
