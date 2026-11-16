using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.utils
{
    public static partial class RegexPatterns
    {
        // Remove the `.` between two uppercase letters. (Cope with acronym)
        [GeneratedRegex(@"([A-Z])\s*\.\s*([A-Z])(?![A-Za-z]+)")]
        public static partial Regex Acronym();

        // If an acronym is followed by a word, preserve the space between them.
        [GeneratedRegex(@"([A-Z])\s*\.\s*([A-Z])(?=[A-Za-z]+)")]
        public static partial Regex AcronymWithWords();

        // Remove redundant spaces and `\n` around punctuation.
        [GeneratedRegex(@"\s*([.!?,])\s*")]
        public static partial Regex PunctuationSpace();

        // If it is Chinese or Japanese punctuation, no need to insert spaces.
        [GeneratedRegex(@"\s*([。！？，、])\s*")]
        public static partial Regex CJPunctuationSpace();

        // A translation may start with notices in square brackets ("[ 350 ms] ", "[ERROR] "). Only the brackets at
        // the very start count: a greedy `.+` would also swallow the text up to a later "] " in the sentence.
        // Group 1: the notices; group 2: the rest, which may have several lines.
        [GeneratedRegex(@"^((?:\[[^\]\r\n]+\] )+)?(.*)$", RegexOptions.Singleline)]
        public static partial Regex NoticePrefixAndTranslation();

        [GeneratedRegex(@"^(?:\[[^\]\r\n]+\] )+")]
        public static partial Regex NoticePrefix();

        [GeneratedRegex(@"^(https?:\/\/)")]
        public static partial Regex HttpPrefix();

        [GeneratedRegex(@"\/{2,}")]
        public static partial Regex MultipleSlashes();

        // The reasoning some models put in front of their answer. It nearly always has several lines, so `.` must
        // match line breaks too; a block that was cut off before its end (token limit) is reasoning to the last
        // character.
        [GeneratedRegex(@"<think>.*?(?:</think>\s*|$)", RegexOptions.Singleline)]
        public static partial Regex ModelThinking();

        [GeneratedRegex(@"🔤\s*(.+?)\s*🔤", RegexOptions.Singleline)]
        public static partial Regex TargetSentence();
    }
}
