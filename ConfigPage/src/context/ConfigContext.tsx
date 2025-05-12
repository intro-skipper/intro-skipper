import { createContext, useContext, useState, ReactNode, useEffect } from 'react';
import apiClient, { PluginConfiguration } from '../api/apiClient';

// Default configuration with sensible defaults
const defaultConfig: PluginConfiguration = {
    AutoDetectIntros: true,
    UpdateMediaSegments: false,
    AnalyzeMovies: false,
    AnalyzeSeasonZero: false,
    SelectAllLibraries: true,
    SelectedLibraries: '',
    ExcludeSeries: '',
    ScanIntroduction: true,
    ScanCredits: true,
    ScanRecap: false,
    ScanPreview: false,
    AnalysisPercent: 25,
    AnalysisLengthLimit: 10,
    MinimumIntroDuration: 10,
    MaximumIntroDuration: 120,
    MinimumCreditsDuration: 20,
    MaximumCreditsDuration: 300,
    MaximumMovieCreditsDuration: 600,
    MinimumRecapDuration: 20,
    MaximumRecapDuration: 300,
    MinimumPreviewDuration: 20,
    MaximumPreviewDuration: 300,
    PreferChromaprint: true,
    FullLengthChapters: false,
    AdjustIntroBasedOnSilence: false,
    SilenceDetectionMaximumNoise: -30,
    SilenceDetectionMinimumDuration: 0.1,
    SnapToKeyframe: false,
    AdjustIntroBasedOnChapters: false,
    AdjustWindowInward: 5,
    AdjustWindowOutward: 5,
    PluginSkip: true,
    AutoSkip: false,
    ClientList: '',
    TypeList: 'Introduction, Credits',
    SkipFirstEpisode: true,
    IntroEndOffset: 0,
    IntroStartOffset: 0,
    AutoSkipDelay: 0,
    UseAlternativeBlackFrameAnalyzer: false,
    BlackFrameMinimumPercentage: 98,
    BlackFrameThreshold: 0.1,
    ChapterAnalyzerIntroductionPattern: 'intro',
    ChapterAnalyzerEndCreditsPattern: 'credits|end',
    ChapterAnalyzerPreviewPattern: 'preview|next',
    ChapterAnalyzerRecapPattern: 'recap|previously',
    ProcessPriority: 'Normal',
    ProcessThreads: 4,
    AutoSkipNotificationText: 'Skipped %segmenttype from %start to %end (%duration)',
    RebuildMediaSegments: false,
    CacheFingerprints: true,
    UseChapterMarkersBlackFrame: false
};

interface ConfigContextType {
  config: PluginConfiguration;
  setConfig: (config: PluginConfiguration) => void;
  loading: boolean;
  error: string | null;
  saveConfig: () => Promise<void>;
  fetchConfig: () => Promise<void>;
}

const ConfigContext = createContext<ConfigContextType | undefined>(undefined);

export const ConfigProvider = ({ children }: { children: ReactNode }) => {
  const [config, setConfig] = useState<PluginConfiguration>(defaultConfig);
  const [loading, setLoading] = useState<boolean>(true);
  const [error, setError] = useState<string | null>(null);

  // Fetch configuration on initial load
  const fetchConfig = async () => {
    try {
      setLoading(true);
      setError(null);
      const pluginConfig = await apiClient.getPluginConfiguration();
      setConfig(pluginConfig);
    } catch (err) {
      setError('Failed to load configuration');
      console.error(err);
    } finally {
      setLoading(false);
    }
  };

  // Save configuration
  const saveConfig = async () => {
    try {
      setLoading(true);
      setError(null);
      await apiClient.updatePluginConfiguration(config);
      // Optionally refetch to ensure we have the latest
      await fetchConfig();
    } catch (err) {
      setError('Failed to save configuration');
      console.error(err);
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchConfig();
  }, []);

  return (
    <ConfigContext.Provider value={{
      config,
      setConfig,
      loading,
      error,
      saveConfig,
      fetchConfig
    }}>
      {children}
    </ConfigContext.Provider>
  );
};

export const useConfig = (): ConfigContextType => {
  const context = useContext(ConfigContext);
  if (context === undefined) {
    throw new Error('useConfig must be used within a ConfigProvider');
  }
  return context;
};
