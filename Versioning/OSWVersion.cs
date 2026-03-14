namespace OSWTools
{
    /// <summary>
    /// Single source of truth for the DLL version and GitHub release location.
    /// Bump Current before each build, then tag the release on GitHub.
    /// </summary>
    public static class OSWVersion
    {
        public const string Current     = "1.0.0";
        public const string GitHubOwner = "OSUPhoenix";
        public const string GitHubRepo  = "OSWTools";

        public static string GitHubApiLatest
        {
            get { return "https://api.github.com/repos/" + GitHubOwner + "/" + GitHubRepo + "/releases/latest"; }
        }

        public static string GitHubDllDownload
        {
            get { return "https://github.com/" + GitHubOwner + "/" + GitHubRepo + "/releases/latest/download/OSWTools.dll"; }
        }
    }
}
