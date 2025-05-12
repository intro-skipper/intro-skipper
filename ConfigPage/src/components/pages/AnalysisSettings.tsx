import { useQuery } from '@tanstack/react-query';
import { Section, Checkbox, InputField, CollapsibleSection, CheckboxList } from '../ui/FormComponents';
import { useConfig } from '../../context/ConfigContext';
import apiClient from '../../api/apiClient';

const AnalysisSettings = () => {
  const { config, setConfig } = useConfig();

  // Fetch libraries for selection
  const { data: libraries = [] } = useQuery({
    queryKey: ['libraries'],
    queryFn: () => apiClient.getLibraries(),
    select: (data) => data.filter(lib =>
      lib.CollectionType === undefined ||
      lib.CollectionType === 'tvshows' ||
      lib.CollectionType === 'movies'
    )
  });

  // Helper to update a specific field in the config
  const updateConfig = (field: string, value: any) => {
    setConfig({ ...config, [field]: value });
  };

  // Parse selected libraries from comma-separated string
  const selectedLibraries = config.SelectedLibraries
    ? config.SelectedLibraries.split(',').map(lib => lib.trim())
    : [];

  // Handle library selection change
  const handleLibrariesChange = (selected: string[]) => {
    updateConfig('SelectedLibraries', selected.join(', '));
  };

  return (
    <div>
      <Section title="Analysis Settings">
        <Checkbox
          id="AutoDetectIntros"
          label="Automatically Analyze New Media"
          checked={config.AutoDetectIntros}
          onChange={(checked) => updateConfig('AutoDetectIntros', checked)}
          description="If enabled, new media will be automatically analyzed for skippable segments when added to the library."
        />

        <Checkbox
          id="UpdateMediaSegments"
          label="Update Missing Segments During Scan"
          checked={config.UpdateMediaSegments}
          onChange={(checked) => updateConfig('UpdateMediaSegments', checked)}
          description="Enable this option to update media segments for any uncached media during a library scan. This includes recently added, modified, or previously skipped (but not ignored) files."
        />

        <Checkbox
          id="AnalyzeMovies"
          label="Analyze Movies"
          checked={config.AnalyzeMovies}
          onChange={(checked) => updateConfig('AnalyzeMovies', checked)}
        />

        <Checkbox
          id="AnalyzeSeasonZero"
          label="Analyze Season 0 (Specials / Extras)"
          checked={config.AnalyzeSeasonZero}
          onChange={(checked) => updateConfig('AnalyzeSeasonZero', checked)}
          description="Note: Shows containing both a specials and extra folder will identify extras as season 0 and ignore specials, regardless of this setting."
        />

        <Checkbox
          id="SelectAllLibraries"
          label="Enable analysis for all libraries (uncheck to limit analysis to specific libraries)"
          checked={config.SelectAllLibraries}
          onChange={(checked) => updateConfig('SelectAllLibraries', checked)}
        />

        {!config.SelectAllLibraries && libraries.length > 0 && (
          <div className="folderAccessListContainer" style={{ marginBottom: '-1em' }}>
            <CheckboxList
              title="Limit analysis to the following libraries"
              items={libraries.map(lib => lib.Name || 'Unnamed Library')}
              selectedItems={selectedLibraries}
              onChange={handleLibrariesChange}
            />
          </div>
        )}

        <InputField
          id="ExcludeSeries"
          label="Exclude series"
          value={config.ExcludeSeries}
          onChange={(value) => updateConfig('ExcludeSeries', value)}
          description="Exclude series from analysis. Enter a comma-separated list of series names to exclude."
        />
      </Section>

      <CollapsibleSection title="Modify Analysis Parameters">
        <p>
          <b style={{ color: 'orange' }}>Changing segment parameters requires regenerating media segments before changes take effect.</b>
          <br />
          Per the jellyfin MediaSegments API, records must be updated individually and may be slow to regenerate.
        </p>

        <Checkbox
          id="ScanIntroduction"
          label="Identify Introductions"
          checked={config.ScanIntroduction}
          onChange={(checked) => updateConfig('ScanIntroduction', checked)}
        />

        <Checkbox
          id="ScanCredits"
          label="Identify Credits"
          checked={config.ScanCredits}
          onChange={(checked) => updateConfig('ScanCredits', checked)}
        />

        <Checkbox
          id="ScanRecap"
          label="Identify Recaps"
          checked={config.ScanRecap}
          onChange={(checked) => updateConfig('ScanRecap', checked)}
        />

        <Checkbox
          id="ScanPreview"
          label="Identify Previews"
          checked={config.ScanPreview}
          onChange={(checked) => updateConfig('ScanPreview', checked)}
        />

        <InputField
          id="AnalysisPercent"
          label="Percent of media to analyze"
          value={config.AnalysisPercent}
          onChange={(value) => updateConfig('AnalysisPercent', value)}
          type="number"
          min={1}
          max={90}
          description="Analysis will be limited to this percentage of each item's runtime. For example, a value of 25 (the default) will limit analysis to the first quarter of each item."
        />

        <InputField
          id="AnalysisLengthLimit"
          label="Maximum runtime to analyze (in minutes)"
          value={config.AnalysisLengthLimit}
          onChange={(value) => updateConfig('AnalysisLengthLimit', value)}
          type="number"
          min={1}
          description="Analysis will be limited to this amount of each item's runtime. For example, a value of 10 (the default) will limit analysis to the first 10 minutes of each item."
        />

        <p>The amount of each item's content that will be analyzed is determined using the percentage and maximum runtime. The minimum of (duration * percent, maximum runtime) is the amount that will be analyzed.</p>
        <p>If the percentage or maximum runtime settings are modified, the cached fingerprints and timestamps for each series, season, or movie you want to analyze with the modified settings <b>will have to be recreated</b>.</p>
        <p>Increasing either of the above settings will cause episode analysis to take much longer.</p>
        <br />

        <Checkbox
          id="PreferChromaprint"
          label="Prefer Chromaprint Analysis"
          checked={config.PreferChromaprint}
          onChange={(checked) => updateConfig('PreferChromaprint', checked)}
          description="Setting an analysis mode in the advanced options will override this setting."
        />

        <Checkbox
          id="FullLengthChapters"
          label="Ignore duration limits for chapters"
          checked={config.FullLengthChapters}
          onChange={(checked) => updateConfig('FullLengthChapters', checked)}
        />

        <InputField
          id="MinimumIntroDuration"
          label="Minimum introduction duration (in seconds)"
          value={config.MinimumIntroDuration}
          onChange={(value) => updateConfig('MinimumIntroDuration', value)}
          type="number"
          min={1}
          description="Segments or similar sounding audio which is shorter than this duration will not be considered an introduction."
        />

        <InputField
          id="MaximumIntroDuration"
          label="Maximum introduction duration (in seconds)"
          value={config.MaximumIntroDuration}
          onChange={(value) => updateConfig('MaximumIntroDuration', value)}
          type="number"
          min={1}
          description="Segments or similar sounding audio which is longer than this duration will not be considered an introduction."
        />

        <InputField
          id="MinimumCreditsDuration"
          label="Minimum credits duration (in seconds)"
          value={config.MinimumCreditsDuration}
          onChange={(value) => updateConfig('MinimumCreditsDuration', value)}
          type="number"
          min={1}
          description="Segments or similar sounding audio which is shorter than this duration will not be considered credits."
        />

        <InputField
          id="MaximumCreditsDuration"
          label="Maximum credits duration (in seconds)"
          value={config.MaximumCreditsDuration}
          onChange={(value) => updateConfig('MaximumCreditsDuration', value)}
          type="number"
          min={1}
          description="Segments or similar sounding audio which is longer than this duration will not be considered credits."
        />

        {config.AnalyzeMovies && (
          <InputField
            id="MaximumMovieCreditsDuration"
            label="Maximum movie credits duration (in seconds)"
            value={config.MaximumMovieCreditsDuration}
            onChange={(value) => updateConfig('MaximumMovieCreditsDuration', value)}
            type="number"
            min={1}
            description="Segments longer than this duration will not be considered movie credits."
          />
        )}

        {!config.FullLengthChapters && (
          <div id="RecapPreviewDurations">
            <InputField
              id="MinimumRecapDuration"
              label="Minimum recap duration (in seconds)"
              value={config.MinimumRecapDuration}
              onChange={(value) => updateConfig('MinimumRecapDuration', value)}
              type="number"
              min={1}
              description="Segments which are shorter than this duration will not be considered a recap."
            />

            <InputField
              id="MaximumRecapDuration"
              label="Maximum recap duration (in seconds)"
              value={config.MaximumRecapDuration}
              onChange={(value) => updateConfig('MaximumRecapDuration', value)}
              type="number"
              min={1}
              description="Segments which are longer than this duration will not be considered a recap."
            />

            <InputField
              id="MinimumPreviewDuration"
              label="Minimum preview duration (in seconds)"
              value={config.MinimumPreviewDuration}
              onChange={(value) => updateConfig('MinimumPreviewDuration', value)}
              type="number"
              min={1}
              description="Segments which are shorter than this duration will not be considered a preview."
            />

            <InputField
              id="MaximumPreviewDuration"
              label="Maximum preview duration (in seconds)"
              value={config.MaximumPreviewDuration}
              onChange={(value) => updateConfig('MaximumPreviewDuration', value)}
              type="number"
              min={1}
              description="Segments which are longer than this duration will not be considered a preview."
            />
          </div>
        )}
      </CollapsibleSection>
    </div>
  );
};

export default AnalysisSettings;
