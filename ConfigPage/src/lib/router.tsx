import {
  Router,
  createHashHistory,
  createRootRoute,
  createRoute
} from '@tanstack/react-router';
import MainLayout from '../components/layouts/MainLayout';
import AnalysisSettings from '../components/pages/AnalysisSettings';
import PlaybackSettings from '../components/pages/PlaybackSettings';
import AdvancedSettings from '../components/pages/AdvancedSettings';
import NotFound from '../components/pages/NotFound';

// Define root route
const rootRoute = createRootRoute({
  component: MainLayout,
});

// Define routes using the MainLayout as parent
const analysisRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/configurationpage',
  component: AnalysisSettings,
});

const playbackRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/configurationpage/playback',
  component: PlaybackSettings,
});

const advancedRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/configurationpage/advanced',
  component: AdvancedSettings,
});

const notFoundRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '*',
  component: NotFound,
});

// Create the route tree
const routeTree = rootRoute.addChildren([
  analysisRoute,
  playbackRoute,
  advancedRoute,
  notFoundRoute,
]);

// Create the router
const hashHistory = createHashHistory();
const router = new Router({
  routeTree,
  history: hashHistory,
});

// Register the router for type-safety
declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router
  }
}

export default router;
