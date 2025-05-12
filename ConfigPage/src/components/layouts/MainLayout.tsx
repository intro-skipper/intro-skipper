import { Link, Outlet } from '@tanstack/react-router';
import { useConfig } from '../../context/ConfigContext';
import { Button } from '../ui/FormComponents';

const MainLayout = () => {
  const { saveConfig, loading, error } = useConfig();

  return (
    <div className="page type-interior pluginConfigurationPage">
      <div data-role="content">
        <div className="content-primary">
          <div className="verticalSection">
            <h1>Intro Skipper Configuration</h1>

            {error && (
              <div className="alert alert-danger">{error}</div>
            )}

            <div className="tabs">
              <Link
                to="/configurationpage"
                activeProps={{ className: 'tabItem tabItem-active' }}
                inactiveProps={{ className: 'tabItem' }}
              >
                Analysis
              </Link>
              <Link
                to="/configurationpage/playback"
                activeProps={{ className: 'tabItem tabItem-active' }}
                inactiveProps={{ className: 'tabItem' }}
              >
                Playback
              </Link>
              <Link
                to="/configurationpage/advanced"
                activeProps={{ className: 'tabItem tabItem-active' }}
                inactiveProps={{ className: 'tabItem' }}
              >
                Advanced
              </Link>
            </div>

            <form onSubmit={(e) => {
              e.preventDefault();
              saveConfig();
            }}>
              {/* This is where the routed content will be rendered */}
              <Outlet />

              <div className="verticalSection-extrabottompadding">
                <Button
                  type="submit"
                  fullWidth
                  onClick={() => {}}
                  disabled={loading}
                >
                  {loading ? 'Saving...' : 'Save'}
                </Button>
              </div>
            </form>
          </div>
        </div>
      </div>
    </div>
  );
};

export default MainLayout;

