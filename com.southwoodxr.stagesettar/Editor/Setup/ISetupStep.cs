namespace StageSettAR.Editor
{
    public enum SetupStatus
    {
        /// <summary>Nothing to do.</summary>
        Done,

        /// <summary>The step can run now.</summary>
        Needed,

        /// <summary>The step cannot run yet (an earlier step must finish first).</summary>
        Blocked,
    }

    /// <summary>
    /// One row of the StageSettAR Setup window. Implementations are found with TypeCache, so a
    /// step can live in any editor assembly, including ones that only compile once an optional
    /// package (UnitySplats, AR Foundation, XRI) is installed.
    /// </summary>
    /// <remarks>Implementations need a public parameterless constructor and must be cheap to query: the window calls <see cref="GetStatus"/> on every repaint.</remarks>
    public interface ISetupStep
    {
        /// <summary>Position in the window, ascending.</summary>
        int Order { get; }

        string Title { get; }

        /// <summary>Current state, plus one short sentence for the user explaining it.</summary>
        SetupStatus GetStatus(out string detail);

        /// <summary>Button label, e.g. "Install" or "Create".</summary>
        string ActionLabel { get; }

        /// <summary>Performs the step. Called only when <see cref="GetStatus"/> returned <see cref="SetupStatus.Needed"/>.</summary>
        void Run();
    }
}
