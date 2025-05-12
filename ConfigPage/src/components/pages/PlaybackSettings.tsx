import { useQuery } from '@tanstack/react-query';
import { Section, Checkbox, InputField, CheckboxList } from '../ui/FormComponents';
import { useConfig } from '../../context/ConfigContext';
import apiClient from '../../api/apiClient';

const PlaybackSettings = () => {
  const { config, setConfig } = useConfig();

  // Fetch devices for client selection
  const { data: devices = [] } = useQuery({
    queryKey: ['devices'],
    queryFn: () => apiClient.getDevices(),
    select: (data) => [...new Set(data.map(device => device.AppName))]
  });

  // Helper to update a specific field in the config
  const updateConfig = (field: string, value: any) => {
    setConfig({ ...config, [field]: value });
  };

  // Parse selected clients and types from comma-separated string
  const selectedClients = config.ClientList
    ? config.ClientList.split(',').map(client => client.trim())
    : [];

  const selectedTypes = config.TypeList
    ? config.TypeList.split(',').map(type => type.trim())
    : [];

  // Handle client selection change
  const handleClientsChange = (selected: string[]) => {
    updateConfig('ClientList', selected.join(', '));
  };

  // Handle types selection change
  const handleTypesChange = (selected: string[]) => {
    updateConfig('TypeList', selected.join(', '));
  };

  // Available segment types
  const segmentTypes = ["Introduction", "Credits", "Recap", "Preview"];

  return (
    <div>
      <Section title="Server-side Skip Settings">
        <Checkbox
          id="PluginSkip"
          label="Enable Server-side Auto Skip"
          checked={config.PluginSkip}
          onChange={(checked) => updateConfig('PluginSkip', checked)}
        />

        {config.PluginSkip && (
          <>
            <Checkbox
              id="AutoSkip"
              label="Automatically Skip for All Clients"
              checked={config.AutoSkip}
              onChange={(checked) => updateConfig('AutoSkip', checked)}
            />

            {!config.AutoSkip && devices.length > 0 && (
              <div className="AutoSkipClientListContainer">
                <CheckboxList
                  title="Limit auto skip to the following clients"
                  items={devices}
                  selectedItems={selectedClients}
                  onChange={handleClientsChange}
                />
              </div>
            )}

            <div className="AutoSkipTypeListContainer">
              <CheckboxList
                title="Auto skip the following types"
                items={segmentTypes}
                selectedItems={selectedTypes}
                onChange={handleTypesChange}
              />
            </div>

            <div id="divSkipFirstEpisode">
              <Checkbox
                id="SkipFirstEpisode"
                label="Play Segments for First Episode of a Season"
                checked={config.SkipFirstEpisode}
                onChange={(checked) => updateConfig('SkipFirstEpisode', checked)}
                description="If checked, auto skip will play the segments of the first episode in a season."
              />
            </div>

            <div id="divAutoSkipDelay">
              <InputField
                id="AutoSkipDelay"
                label="Auto skip delay (in seconds)"
                value={config.AutoSkipDelay}
                onChange={(value) => updateConfig('AutoSkipDelay', value)}
                type="number"
                min={0}
                description="Seconds at the start of a segment that should be played before skipping. Defaults to 0."
              />
            </div>

            <InputField
              id="IntroStartOffset"
              label="Intro Start Offset (seconds)"
              value={config.IntroStartOffset}
              onChange={(value) => updateConfig('IntroStartOffset', value)}
              type="number"
              min={0}
              step={0.5}
              description="Default: 0. Example: If set to 3, playback will skip 3 seconds past the start of the intro."
            />

            <InputField
              id="IntroEndOffset"
              label="Intro End Offset (seconds)"
              value={config.IntroEndOffset}
              onChange={(value) => updateConfig('IntroEndOffset', value)}
              type="number"
              min={0}
              step={0.5}
              description="Default: 0. Example: If set to 3, playback will resume 3 seconds before the end of the intro."
            />

            <div id="divAutoSkipNotificationText">
              <InputField
                id="AutoSkipNotificationText"
                label="Auto skip notification message"
                value={config.AutoSkipNotificationText}
                onChange={(value) => updateConfig('AutoSkipNotificationText', value)}
                description="Message shown after automatically skipping a segment. Leave blank to disable notification. Available variables: %segmenttype, %start, %end, %duration"
              />
              <p>
                <b style={{ color: 'orange' }}>This setting does not apply to Media Segment Actions in Jellyfin 10.10 and compatible clients.</b>
              </p>
            </div>
          </>
        )}
      </Section>
    </div>
  );
};

export default PlaybackSettings;
