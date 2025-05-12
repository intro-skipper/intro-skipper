import { useState } from 'react';
import { Section, Checkbox, InputField, CollapsibleSection, Button } from '../ui/FormComponents';
import { useConfig } from '../../context/ConfigContext';
import apiClient from '../../api/apiClient';

const AdvancedSettings = () => {
  const { config, setConfig } = useConfig();
  const [supportBundle, setSupportBundle] = useState('');
  const [storageInfo, setStorageInfo] = useState('');
  const [isLoading, setIsLoading] = useState({
    supportBundle: false,
    storageInfo: false,
    rebuildDatabase: false,
    eraseTimestamps: false
  });

  // Helper to update a specific field in the config
  const updateConfig = (field: string, value: any) => {
    setConfig({ ...config, [field]: value });
  };

  // Handler for getting support bundle
  const handleGetSupportBundle = async () => {
    setIsLoading(prev => ({ ...prev, supportBundle: true }));
    try {
      const bundle = await apiClient.getSupportBundle();
      setSupportBundle(bundle);
    } catch (error) {
      console.error('Failed to get support bundle:', error);
    } finally {
      setIsLoading(prev => ({ ...prev, supportBundle: false }));
    }
  };

  // Handler for getting storage info
  const handleGetStorageInfo = async () => {
    setIsLoading(prev => ({ ...prev, storageInfo: true }));
    try {
      const info = await apiClient.getStorage();
      setStorageInfo(info);
    } catch (error) {
      console.error('Failed to get storage info:', error);
    } finally {
      setIsLoading(prev => ({ ...prev, storageInfo: false }));
    }
  };

  // Handler for rebuilding database
  const handleRebuildDatabase = async () => {
    setIsLoading(prev => ({ ...prev, rebuildDatabase: true }));
    try {
      await apiClient.rebuildDatabase();
      alert('Database rebuild initiated. Jellyfin must be restarted to complete this process.');
    } catch (error) {
      console.error('Failed to rebuild database:', error);
      alert('Failed to rebuild database. Please check the console for more information.');
    } finally {
      setIsLoading(prev => ({ ...prev, rebuildDatabase: false }));
    }
  };

  // Handler for erasing timestamps
  const handleEraseTimestamps = async (mode: string, eraseCache = false) => {
    if (!window.confirm(`Are you sure you want to erase all ${mode} timestamps?`)) {
      return;
    }

    setIsLoading(prev => ({ ...prev, eraseTimestamps: true }));
    try {
      await apiClient.eraseTimestamps(mode, eraseCache);
      alert(`Successfully erased ${mode} timestamps.`);
    } catch (error) {
      console.error(`Failed to erase ${mode} timestamps:`, error);
      alert(`Failed to erase ${mode} timestamps. Please check the console for more information.`);
    } finally {
      setIsLoading(prev => ({ ...prev, eraseTimestamps: false }));
    }
  };

  return (
    <div>
      <Section title="Detection Adjustment Options">
        <Checkbox
          id="AdjustIntroBasedOnSilence"
          label="Enable silence detection"
          checked={config.AdjustIntroBasedOnSilence}
          onChange={(checked) => updateConfig('AdjustIntroBasedOnSilence', checked)}
          description="When enabled, segment endpoints will be adjusted to the nearest silence point."
        />

        {config.AdjustIntroBasedOnSilence && (
          <div id="silenceSettings">
            <InputField
              id="SilenceDetectionMaximumNoise"
              label="Noise tolerance"
              value={config.SilenceDetectionMaximumNoise}
              onChange={(value) => updateConfig('SilenceDetectionMaximumNoise', value)}
              type="number"
              min={-90}
              max={0}
              description="Noise tolerance in negative decibels."
            />

            <InputField
              id="SilenceDetectionMinimumDuration"
              label="Minimum silence duration"
              value={config.SilenceDetectionMinimumDuration}
              onChange={(value) => updateConfig('SilenceDetectionMinimumDuration', value)}
              type="number"
              min={0}
              step={0.01}
              description="Minimum silence duration in seconds before adjusting introduction end time."
            />
          </div>
        )}

        <Checkbox
          id="SnapToKeyframe"
          label="Enable keyframe snapping"
          checked={config.SnapToKeyframe}
          onChange={(checked) => updateConfig('SnapToKeyframe', checked)}
          description="When enabled, segment endpoints will be adjusted to the nearest video keyframe for smoother seek transitions during skipping."
        />

        <Checkbox
          id="AdjustIntroBasedOnChapters"
          label="Enable chapter snapping"
          checked={config.AdjustIntroBasedOnChapters}
          onChange={(checked) => updateConfig('AdjustIntroBasedOnChapters', checked)}
          description="When enabled, segment start and end times will be adjusted to the nearest chapter boundary."
        />

        <InputField
          id="AdjustWindowInward"
          label="Adjustment window (inward)"
          value={config.AdjustWindowInward}
          onChange={(value) => updateConfig('AdjustWindowInward', value)}
          type="number"
          min={0}
          description="Maximum number of seconds to search toward a segment's interior for adjustment points (like chapter boundaries, silence, or keyframes). Used to tighten segment boundaries."
        />

        <InputField
          id="AdjustWindowOutward"
          label="Adjustment window (outward)"
          value={config.AdjustWindowOutward}
          onChange={(value) => updateConfig('AdjustWindowOutward', value)}
          type="number"
          min={0}
          description="Maximum number of seconds to search away from a segment for adjustment points (like chapter boundaries, silence, or keyframes). Used to expand segment boundaries."
        />
      </Section>

      <Section title="Black Frame Detection Options">
        <Checkbox
          id="UseAlternativeBlackFrameAnalyzer"
          label="Use alternative black frame analyzer (experimental)"
          checked={config.UseAlternativeBlackFrameAnalyzer}
          onChange={(checked) => updateConfig('UseAlternativeBlackFrameAnalyzer', checked)}
          description="If enabled, the alternative black frame analyzer will be used. This analyzer is experimental and may not work as expected."
        />

        {!config.UseAlternativeBlackFrameAnalyzer && (
          <div id="chapterMarkersBlackFrameSetting">
            <Checkbox
              id="UseChapterMarkersBlackFrame"
              label="Use chapter markers from black frame detection"
              checked={config.UseChapterMarkersBlackFrame}
              onChange={(checked) => updateConfig('UseChapterMarkersBlackFrame', checked)}
              description="If enabled, chapter markers will be created based on detected black frames."
            />
          </div>
        )}

        <InputField
          id="BlackFrameMinimumPercentage"
          label="Black frame minimum percentage"
          value={config.BlackFrameMinimumPercentage}
          onChange={(value) => updateConfig('BlackFrameMinimumPercentage', value)}
          type="number"
          min={0}
          max={100}
          description="Percentage of the frame that must be black to be considered a black frame."
        />

        <InputField
          id="BlackFrameThreshold"
          label="Black frame threshold"
          value={config.BlackFrameThreshold}
          onChange={(value) => updateConfig('BlackFrameThreshold', value)}
          type="number"
          min={0}
          max={1}
          step={0.01}
          description="Threshold for determining if a pixel is black. Lower values mean darker pixels are required."
        />
      </Section>

      <CollapsibleSection title="Maintenance">
        <Section title="Database Maintenance">
          <p>
            <Button
              variant="danger"
              onClick={handleRebuildDatabase}
              disabled={isLoading.rebuildDatabase}
              fullWidth
            >
              {isLoading.rebuildDatabase ? 'Rebuilding...' : 'Rebuild Local Database'}
            </Button>
          </p>
          <p style={{ textAlign: 'center' }}>
            <b style={{ color: 'red' }}>Rebuilding database requires a full Jellyfin restart to complete, <i>NOT</i> a dashboard restart!</b>
          </p>
        </Section>

        <Section title="Erase Timestamps">
          <select id="GlobalTimestamps" className="emby-select-withcolor emby-select" style={{ marginBottom: '10px', width: '100%' }}>
            <option value="Introduction">Introduction</option>
            <option value="Credits">Credits</option>
            <option value="Recap">Recap</option>
            <option value="Preview">Preview</option>
          </select>

          <div className="checkboxContainer" style={{ margin: '0' }}>
            <label className="emby-checkbox-label">
              <input id="eraseModeCacheCheckbox" type="checkbox" className="emby-checkbox" />
              <span>Include global cached fingerprint files</span>
            </label>
          </div>

          <Button
            variant="danger"
            onClick={() => {
              const mode = (document.getElementById('GlobalTimestamps') as HTMLSelectElement).value;
              const eraseCache = (document.getElementById('eraseModeCacheCheckbox') as HTMLInputElement).checked;
              handleEraseTimestamps(mode, eraseCache);
            }}
            disabled={isLoading.eraseTimestamps}
            fullWidth
          >
            {isLoading.eraseTimestamps ? 'Erasing...' : 'Erase selected timestamps (globally)'}
          </Button>
        </Section>
      </CollapsibleSection>

      <CollapsibleSection title="Intro Skipper Support Log">
        <Button
          onClick={handleGetSupportBundle}
          disabled={isLoading.supportBundle}
          fullWidth
          style={{ marginBottom: '10px' }}
        >
          {isLoading.supportBundle ? 'Loading...' : 'Load Support Information'}
        </Button>

        {supportBundle && (
          <textarea
            style={{ width: '100%', minHeight: '300px', marginTop: '10px' }}
            readOnly
            value={supportBundle}
          />
        )}
      </CollapsibleSection>

      <CollapsibleSection title="Storage Information">
        <Button
          onClick={handleGetStorageInfo}
          disabled={isLoading.storageInfo}
          fullWidth
          style={{ marginBottom: '10px' }}
        >
          {isLoading.storageInfo ? 'Loading...' : 'Load Storage Information'}
        </Button>

        {storageInfo && (
          <textarea
            style={{ width: '100%', minHeight: '300px', marginTop: '10px' }}
            readOnly
            value={storageInfo}
          />
        )}
      </CollapsibleSection>
    </div>
  );
};

export default AdvancedSettings;
