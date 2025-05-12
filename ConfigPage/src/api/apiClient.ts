import axios, { AxiosInstance } from 'axios';

// Plugin ID for IntroSkipper
const PLUGIN_ID = 'c83d86bb-a1e0-4c35-a113-e2101cf4ee6b';

/**
 * Configuration interface based on the plugin's configuration settings
 */
export interface PluginConfiguration {
  UseChapterMarkersBlackFrame: boolean;
  // Analysis settings
  AutoDetectIntros: boolean;
  UpdateMediaSegments: boolean;
  AnalyzeMovies: boolean;
  AnalyzeSeasonZero: boolean;
  SelectAllLibraries: boolean;
  SelectedLibraries: string;
  ExcludeSeries: string;

  // Scan settings
  ScanIntroduction: boolean;
  ScanCredits: boolean;
  ScanRecap: boolean;
  ScanPreview: boolean;

  // Analysis parameters
  AnalysisPercent: number;
  AnalysisLengthLimit: number;
  MinimumIntroDuration: number;
  MaximumIntroDuration: number;
  MinimumCreditsDuration: number;
  MaximumCreditsDuration: number;
  MaximumMovieCreditsDuration: number;
  MinimumRecapDuration: number;
  MaximumRecapDuration: number;
  MinimumPreviewDuration: number;
  MaximumPreviewDuration: number;

  // Analysis preferences
  PreferChromaprint: boolean;
  FullLengthChapters: boolean;

  // Adjustment settings
  AdjustIntroBasedOnSilence: boolean;
  SilenceDetectionMaximumNoise: number;
  SilenceDetectionMinimumDuration: number;
  SnapToKeyframe: boolean;
  AdjustIntroBasedOnChapters: boolean;
  AdjustWindowInward: number;
  AdjustWindowOutward: number;

  // Skip settings
  PluginSkip: boolean;
  AutoSkip: boolean;
  ClientList: string;
  TypeList: string;
  SkipFirstEpisode: boolean;
  IntroEndOffset: number;
  IntroStartOffset: number;
  AutoSkipDelay: number;

  // Black frame detection
  UseAlternativeBlackFrameAnalyzer: boolean;
  BlackFrameMinimumPercentage: number;
  BlackFrameThreshold: number;

  // Chapter patterns
  ChapterAnalyzerIntroductionPattern: string;
  ChapterAnalyzerEndCreditsPattern: string;
  ChapterAnalyzerPreviewPattern: string;
  ChapterAnalyzerRecapPattern: string;

  // Processing settings
  ProcessPriority: string;
  ProcessThreads: number;

  // Notification
  AutoSkipNotificationText: string;

  // Other settings
  RebuildMediaSegments: boolean;
  CacheFingerprints: boolean;
}

export interface Device {
  Id: string;
  Name: string;
  AppName: string;
}

export interface DevicesResponse {
  Items: Device[];
}

export interface Library {
  Name: string;
  CollectionType?: string;
}

/**
 * API Client for interfacing with the Jellyfin server
 */
class ApiClient {
  private client: AxiosInstance;
  private serverAddress = '';
  private accessToken = '';

  constructor() {
    this.client = axios.create();

    // Initialize from window.ApiClient if it exists
    if (typeof window !== 'undefined' && window.ApiClient) {
      this.serverAddress = window.ApiClient.serverAddress?.() || '';
      this.accessToken = window.ApiClient.accessToken?.() || '';
    }

    // Configure axios instance with auth header
    this.client.interceptors.request.use(config => {
      if (this.accessToken) {
        config.headers.Authorization = `MediaBrowser Token=${this.accessToken}`;
      }
      return config;
    });
  }

  /**
   * Set the server address and access token for API calls
   */
  public setCredentials(serverAddress: string, accessToken: string) {
    this.serverAddress = serverAddress;
    this.accessToken = accessToken;
  }

  /**
   * Get the full URL for an API endpoint
   */
  private getUrl(endpoint: string): string {
    return `${this.serverAddress}/${endpoint}`;
  }

  /**
   * Get the plugin configuration
   */
  public async getPluginConfiguration(): Promise<PluginConfiguration> {
    try {
      const response = await this.client.get(this.getUrl(`Plugins/${PLUGIN_ID}/Configuration`));
      return response.data;
    } catch (error) {
      console.error('Failed to get plugin configuration:', error);
      throw error;
    }
  }

  /**
   * Update the plugin configuration
   */
  public async updatePluginConfiguration(config: PluginConfiguration) {
    try {
      const response = await this.client.post(
        this.getUrl(`Plugins/${PLUGIN_ID}/Configuration`),
        config
      );
      return response.data;
    } catch (error) {
      console.error('Failed to update plugin configuration:', error);
      throw error;
    }
  }

  /**
   * Get list of devices
   */
  public async getDevices(): Promise<Device[]> {
    try {
      const response = await this.client.get<DevicesResponse>(this.getUrl('Devices'));
      return response.data.Items;
    } catch (error) {
      console.error('Failed to get devices:', error);
      throw error;
    }
  }

  /**
   * Get libraries for the server
   */
  public async getLibraries(): Promise<Library[]> {
    try {
      const response = await this.client.get<Library[]>(this.getUrl('Library/VirtualFolders'));
      return response.data;
    } catch (error) {
      console.error('Failed to get libraries:', error);
      throw error;
    }
  }

  /**
   * Rebuild database
   */
  public async rebuildDatabase(): Promise<void> {
    try {
      await this.client.post(this.getUrl('Intros/RebuildDatabase'));
    } catch (error) {
      console.error('Failed to rebuild database:', error);
      throw error;
    }
  }

  /**
   * Get support bundle
   */
  public async getSupportBundle(): Promise<string> {
    try {
      const response = await this.client.get(this.getUrl('IntroSkipper/SupportBundle'));
      return response.data;
    } catch (error) {
      console.error('Failed to get support bundle:', error);
      throw error;
    }
  }

  /**
   * Get storage information
   */
  public async getStorage(): Promise<string> {
    try {
      const response = await this.client.get(this.getUrl('IntroSkipper/Storage'));
      return response.data;
    } catch (error) {
      console.error('Failed to get storage information:', error);
      throw error;
    }
  }

  /**
   * Erase timestamps for a specific analysis mode
   */
  public async eraseTimestamps(mode: string, eraseCache = false): Promise<void> {
    try {
      await this.client.post(this.getUrl(`Intros/EraseTimestamps?mode=${mode}&eraseCache=${eraseCache}`));
    } catch (error) {
      console.error(`Failed to erase ${mode} timestamps:`, error);
      throw error;
    }
  }
}

// Create and export a singleton instance
const apiClientInstance = new ApiClient();
export default apiClientInstance;

// For global access in window object (like the original ApiClient)
declare global {
  interface Window {
    ApiClient: {
      serverAddress?: () => string;
      accessToken?: () => string;
      getPluginConfiguration?: (pluginId: string) => Promise<any>;
      updatePluginConfiguration?: (pluginId: string, config: any) => Promise<any>;
    };
  }
}
