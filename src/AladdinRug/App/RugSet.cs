using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AladdinRug
{
    /// <summary>One rug per monitor, lifted and dropped together.</summary>
    internal sealed class RugSet
    {
        private readonly List<RugWindow> _rugs = new List<RugWindow>();
        private readonly DispatcherTimer _displayDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        private List<DeskGeometry> _layout = new List<DeskGeometry>();
        private bool _quitting;

        public event Action ContextRequested;
        public event Action<string> Notice;

        /// <summary>True when every rug is rolled up (or on its way there).</summary>
        public bool IsRolled => _rugs.Count > 0 && _rugs.All(r => r.IsRolled);

        public RugSet()
        {
            _displayDebounce.Tick += (s, e) => { _displayDebounce.Stop(); RelayIfMonitorsChanged(); };
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }

        public void Begin() => Create(null);
        public void Toggle() { if (IsRolled) Unroll(); else RollUp(); }
        public void RollUp() { foreach (RugWindow r in _rugs) r.RollUp(); }
        public void Unroll() { foreach (RugWindow r in _rugs) r.Unroll(); }

        /// <summary>The broom: every rug sweeps its own monitor's icons in under it.</summary>
        public void Sweep() { foreach (RugWindow r in _rugs) r.Sweep(); }

        /// <summary>What a tidy-up would do (loose files, and the folders they would go in), without doing it.</summary>
        public (int files, int folders) PreviewTidy()
        {
            Organizer.Plan plan = Organizer.MakePlan(TidyJob.FilesToTidy(), null, new Random());
            return (plan.FileCount, plan.FolderCount);
        }

        private RugWindow Host
        {
            get
            {
                DeskGeometry main = DeskGeometry.Primary();
                return _rugs.FirstOrDefault(r => r.Geometry.Equals(main)) ?? _rugs.FirstOrDefault();
            }
        }

        private bool _sweepAfterTidy;

        /// <summary>The merchant sorts the desktop's loose files into folders by type. The rug on the main monitor hosts him.</summary>
        public void Tidy() => Host?.Tidy();

        /// <summary>
        /// What happens when the rug starts (if the user has agreed): once the rug is out, the merchant sorts the loose files into
        /// folders, then sweeps the folders under the rugs. Quietly: no messages unless something goes wrong.
        /// </summary>
        public void StartupChores()
        {
            RugWindow host = Host;
            if (host == null) return;
            _sweepAfterTidy = RugStyles.AutoSweep;                  // sweeping is extra: only if the user has turned it on
            host.Tidy(quiet: true);
        }

        private void OnTidyDone()
        {
            if (!_sweepAfterTidy || _quitting) return;
            _sweepAfterTidy = false;
            foreach (RugWindow r in _rugs) r.Sweep(quiet: true);
        }

        /// <summary>Put every file back that a tidy-up moved, and take away the folders it made.</summary>
        public void Untidy()
        {
            int back = Organizer.Undo(out string problem);
            Notice?.Invoke(problem ?? (back + " files are back where they were."));
        }

        /// <summary>Put everything back out from under the rugs, where it was.</summary>
        public void PullOut()
        {
            SweptStore.PutBack(null, out string problem);
            if (problem != null) Notice?.Invoke(problem);
        }

        public void Quit()
        {
            _quitting = true;
            _displayDebounce.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            Clear();                                                // first: any sweep in progress stops and records what it had already tucked under
            SweptStore.PutBack(null, out _);                        // taking the rug away: whatever was under it is out again
        }

        /// <summary>Swap every rug for one of another kind, laid down where the old one was.</summary>
        public void SetStyle(RugStyle style)
        {
            if (_rugs.Count > 0 && style == RugStyles.Current) return;
            Rebuild(() => RugStyles.Current = style);
        }

        /// <summary>Put Aladdin on the rug, or send him away.</summary>
        public void SetAladdin(bool on)
        {
            if (_rugs.Count > 0 && on == RugStyles.Aladdin) return;
            Rebuild(() => RugStyles.Aladdin = on);
        }

        private void Rebuild(Action change)
        {
            var centres = new List<(int x, int y)?>();
            foreach (RugWindow r in _rugs) centres.Add(r.SaveAndGetCentre());
            change();
            RugStyles.Save();
            Clear();
            Create(centres);
        }

        private void Create(List<(int x, int y)?> centres)
        {
            if (centres == null) _layout = DeskGeometry.All();
            for (int i = 0; i < _layout.Count; i++)
            {
                DeskGeometry geo = _layout[i];
                var rug = new RugWindow(geo, RugStyles.Current, centres != null && i < centres.Count ? centres[i] : null);
                rug.ContextRequested += () => ContextRequested?.Invoke();
                rug.Notice += m => Notice?.Invoke(m);
                rug.TidyDone += OnTidyDone;
                rug.Show();
                rug.Begin();
                _rugs.Add(rug);
            }
        }

        private void Clear()
        {
            foreach (RugWindow r in _rugs) r.Quit();
            _rugs.Clear();
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            _displayDebounce.Stop();
            _displayDebounce.Start();
        }

        // Windows fires the display event for plenty of things; only rebuild if a monitor really changed.
        private void RelayIfMonitorsChanged()
        {
            if (_quitting) return;
            List<DeskGeometry> now = DeskGeometry.All();
            if (now.Count == _layout.Count && now.All(g => _layout.Contains(g))) return;

            Clear();      // each rug saved where it was, so the new ones pick up exactly where these left off
            Create(null);
        }
    }
}
