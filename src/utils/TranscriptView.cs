using System.Collections.ObjectModel;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.utils
{
    // The sentences of the running class as the caption page lists them: every finished sentence, with the refined
    // paragraphs in place of the sentences they cover.
    //
    // A reload changes only the rows that differ: new sentences at the end, or the last few sentences that became
    // a paragraph. Handing the page a new list with every sentence makes it build all its rows again, and by the
    // end of a long class that is thousands of rows every few seconds.
    public sealed class TranscriptView
    {
        public ObservableCollection<HistoryLine> Lines { get; } = new();

        // Raised on the UI thread right after `Lines` changed; not when a reload finds nothing new.
        public event Action? Updated;

        private bool loading;
        private bool again;

        // To be called on the UI thread. Reloads asked for while one is running are folded into one more round.
        public async Task Reload()
        {
            if (loading)
            {
                again = true;
                return;
            }
            loading = true;
            try
            {
                do
                {
                    again = false;
                    long first = ClassSession.FirstHistoryId;
                    List<HistoryLine> lines;
                    try
                    {
                        // Not on the UI thread: the database calls block, whatever their names say.
                        lines = first < 0 ? new List<HistoryLine>() : await Task.Run(() => Load(first));
                    }
                    catch (Exception)
                    {
                        // The list stays as it is; the next sentence loads it again.
                        continue;
                    }
                    Apply(lines);
                } while (again);
            }
            finally
            {
                loading = false;
            }
        }

        private static async Task<List<HistoryLine>> Load(long firstHistoryId)
        {
            var lines = await SectionLogger.LoadHistoryRange(firstHistoryId);
            var paragraphs = await SectionLogger.LoadParagraphs(firstHistoryId);
            return LectureDocument.MergeParagraphs(lines, paragraphs, fadeUnrefined: Refiner.Enabled);
        }

        // Makes `Lines` equal to `lines` with as few changes as possible. Returns whether anything changed.
        public bool Apply(List<HistoryLine> lines)
        {
            int same = 0;
            while (same < Lines.Count && same < lines.Count && Same(Lines[same], lines[same]))
                same++;
            if (same == Lines.Count && same == lines.Count)
                return false;

            // Another class: one change for the whole list instead of one for every row.
            if (same == 0 && Lines.Count > 0)
                Lines.Clear();
            for (int i = same; i < lines.Count; i++)
            {
                if (i >= Lines.Count)
                    Lines.Add(lines[i]);
                else if (!Same(Lines[i], lines[i]))
                    Lines[i] = lines[i];
            }
            while (Lines.Count > lines.Count)
                Lines.RemoveAt(Lines.Count - 1);
            Updated?.Invoke();
            return true;
        }

        private static bool Same(HistoryLine a, HistoryLine b) =>
            a.Id == b.Id && a.Time == b.Time && a.Opacity == b.Opacity &&
            a.SourceText == b.SourceText && a.TranslatedText == b.TranslatedText;
    }
}
