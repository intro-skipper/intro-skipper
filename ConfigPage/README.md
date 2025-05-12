# Intro Skipper Configuration Page

This is the React TypeScript configuration page for the Intro Skipper plugin. It uses Tanstack Router and Tanstack Query for data fetching.

## Development

To run this application:

```bash
pnpm install
pnpm start
```

# Building For Production

To build this application for production:

```bash
pnpm build
```

## Testing

This project uses [Vitest](https://vitest.dev/) for testing. You can run the tests with:

```bash
pnpm test
```

## Styling

This project uses CSS for styling.




## Routing
This project uses [TanStack Router](https://tanstack.com/router). The initial setup is a code based router. Which means that the routes are defined in code (in the `./src/main.jsx` file). If you like you can also use a file based routing setup by following the [File Based Routing](https://tanstack.com/router/latest/docs/framework/react/guide/file-based-routing) guide.

### Adding A Route

To add a new route to your application just add another `createRoute` call to the `./src/main.jsx` file. The example below adds a new `/about`route to the root route.

```tsx
const aboutRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/about",
  component: () => <h1>About</h1>,
});
```

You will also need to add the route to the `routeTree` in the `./src/main.jsx` file.

```tsx
const routeTree = rootRoute.addChildren([indexRoute, aboutRoute]);
```

With this set up you should be able to navigate to `/about` and see the about page.

Of course you don't need to implement the About page in the `main.jsx` file. You can create that component in another file and import it into the `main.jsx` file, then use it in the `component` property of the `createRoute` call, like so:

```tsx
import About from "./components/About.jsx";

const aboutRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/about",
  component: About,
});
```

That is how we have the `App` component set up with the home page.

For more information on the options you have when you are creating code based routes check out the [Code Based Routing](https://tanstack.com/router/latest/docs/framework/react/guide/code-based-routing) documentation.

Now that you have two routes you can use a `Link` component to navigate between them.

### Adding Links

To use SPA (Single Page Application) navigation you will need to import the `Link` component from `@tanstack/react-router`.

```tsx
import { Link } from "@tanstack/react-router";
```

Then anywhere in your JSX you can use it like so:

```tsx
<Link to="/about">About</Link>
```

This will create a link that will navigate to the `/about` route.

More information on the `Link` component can be found in the [Link documentation](https://tanstack.com/router/v1/docs/framework/react/api/router/linkComponent).

### Using A Layout


Layouts can be used to wrap the contents of the routes in menus, headers, footers, etc.

There is already a layout in the `src/main.jsx` file:

```tsx
const rootRoute = createRootRoute({
  component: () => (
    <>
      <Outlet />
      <TanStackRouterDevtools />
    </>
  ),
});
```

You can use the React component specified in the `component` property of the `rootRoute` to wrap the contents of the routes. The `<Outlet />` component is used to render the current route within the body of the layout. For example you could add a header to the layout like so:

```tsx
import { Link } from "@tanstack/react-router";

const rootRoute = createRootRoute({
  component: () => (
    <>
      <header>
        <nav>
          <Link to="/">Home</Link>
          <Link to="/about">About</Link>
        </nav>
      </header>
      <Outlet />
      <TanStackRouterDevtools />
    </>
  ),
});
```

The `<TanStackRouterDevtools />` component is not required so you can remove it if you don't want it in your layout.

More information on layouts can be found in the [Layouts documentation](https://tanstack.com/router/latest/docs/framework/react/guide/routing-concepts#layouts).


### Migrating To File Base Routing

First you need to add the Vite plugin for Tanstack Router:

```bash
pnpm add @tanstack/router-plugin --dev
```

From there you need to update your `vite.config.js` file to use the plugin:

```ts
import { defineConfig } from "vite";
import viteReact from "@vitejs/plugin-react";
import { TanStackRouterVite } from "@tanstack/router-plugin/vite";

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [
    TanStackRouterVite(),
    viteReact()
  ],
});
```

Now you'll need to rearrange your files a little bit. That starts with creating a `routes` directory in the `src` directory:

```bash
mkdir src/routes
```

Then you'll need to create a `src/routes/__root.jsx` file with the contents of the root route that was in `main.jsx`.

```tsx
import { Outlet, createRootRoute } from "@tanstack/react-router";
import { TanStackRouterDevtools } from "@tanstack/react-router-devtools";

export const Route = createRootRoute({
  component: () => (
    <>
      <Outlet />
      <TanStackRouterDevtools />
    </>
  ),
});
```

Next up you'll need to move your home route code into `src/routes/index.jsx`

```tsx
import { createFileRoute } from "@tanstack/react-router";

import logo from "../logo.svg";
import "../App.css";

export const Route = createFileRoute("/")({
  component: App,
});

function App() {
  return (
  <div className="App">
    <header className="App-header">
      <img src={logo} className="App-logo" alt="logo" />
      <p>
        Edit <code>src/App.tsx</code> and save to reload.
      </p>
      <a
        className="App-link"
        href="https://reactjs.org"
        target="_blank"
        rel="noopener noreferrer"
      >
        Learn React
      </a>
      <a
        className="App-link"
        href="https://tanstack.com"
        target="_blank"
        rel="noopener noreferrer"
      >
        Learn TanStack
      </a>
    </header>
  </div>
  );
}
```

At this point you can delete `src/App.jsx`, you will no longer need it as the contents have moved into `src/routes/index.jsx`.

The only additional code is the `createFileRoute` function that tells TanStack Router where to render the route. Helpfully the Vite plugin will keep the path argument that goes to `createFileRoute` automatically in sync with the file system.

Finally the `src/main.jsx` file can be simplified down to this:

```tsx
import { StrictMode } from "react";
import ReactDOM from "react-dom/client";
import { RouterProvider, createRouter } from "@tanstack/react-router";

// Import the generated route tree
import { routeTree } from "./routeTree.gen";

import "./styles.css";
import reportWebVitals from "./reportWebVitals.js";

// Create a new router instance
const router = createRouter({
  routeTree,
  defaultPreload: "intent",
  defaultPreloadStaleTime: 0,
  scrollRestoration: true,
  defaultStructuralSharing: true
});

// Render the app
const rootElement = document.getElementById("app");
if (!rootElement.innerHTML) {
  const root = ReactDOM.createRoot(rootElement);
  root.render(
    <StrictMode>
      <RouterProvider router={router} />
    </StrictMode>
  );
}

// If you want to start measuring performance in your app, pass a function
// to log results (for example: reportWebVitals(console.log))
// or send to an analytics endpoint. Learn more: https://bit.ly/CRA-vitals
reportWebVitals();
```

Now you've got a file based routing setup in your project! Let's have some fun with it! Just create a file in `about.jsx` in `src/routes` and it if the application is running TanStack will automatically add contents to the file and you'll have the start of your `/about` route ready to go with no additional work. You can see why folks find File Based Routing so easy to use.

You can find out everything you need to know on how to use file based routing in the [File Based Routing](https://tanstack.com/router/latest/docs/framework/react/guide/file-based-routing) documentation.

## Data Fetching

There are multiple ways to fetch data in your application. You can use TanStack Query to fetch data from a server. But you can also use the `loader` functionality built into TanStack Router to load the data for a route before it's rendered.

For example:

```tsx
const peopleRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: "/people",
  loader: async () => {
    const response = await fetch("https://swapi.dev/api/people");
    return response.json();
  },
  component: () => {
    const data = peopleRoute.useLoaderData();
    return (
      <ul>
        {data.results.map((person) => (
          <li key={person.name}>{person.name}</li>
        ))}
      </ul>
    );
  },
});
```

Loaders simplify your data fetching logic dramatically. Check out more information in the [Loader documentation](https://tanstack.com/router/latest/docs/framework/react/guide/data-loading#loader-parameters).

### React-Query

React-Query is an excellent addition or alternative to route loading and integrating it into you application is a breeze.

First add your dependencies:

```bash
pnpm add @tanstack/react-query @tanstack/react-query-devtools
```

Next we'll need to create a query client and provider. We recommend putting those in `main.jsx`.

```tsx
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";

// ...

const queryClient = new QueryClient();

// ...

if (!rootElement.innerHTML) {
  const root = ReactDOM.createRoot(rootElement);

  root.render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  );
}
```

You can also add TanStack Query Devtools to the root route (optional).

```tsx
import { ReactQueryDevtools } from "@tanstack/react-query-devtools";

const rootRoute = createRootRoute({
  component: () => (
    <>
      <Outlet />
      <ReactQueryDevtools buttonPosition="top-right" />
      <TanStackRouterDevtools />
    </>
  ),
});
```

Now you can use `useQuery` to fetch your data.

```tsx
import { useQuery } from "@tanstack/react-query";

import "./App.css";

function App() {
  const { data } = useQuery({
    queryKey: ["people"],
    queryFn: () =>
      fetch("https://swapi.dev/api/people")
        .then((res) => res.json())
        .then((data) => data.results),
    initialData: [],
  });

  return (
    <div>
      <ul>
        {data.map((person) => (
          <li key={person.name}>{person.name}</li>
        ))}
      </ul>
    </div>
  );
}

export default App;
```

You can find out everything you need to know on how to use React-Query in the [React-Query documentation](https://tanstack.com/query/latest/docs/framework/react/overview).

## State Management

Another common requirement for React applications is state management. There are many options for state management in React. TanStack Store provides a great starting point for your project.

First you need to add TanStack Store as a dependency:

```bash
pnpm add @tanstack/store
```

Now let's create a simple counter in the `src/App.jsx` file as a demonstration.

```tsx
import { useStore } from "@tanstack/react-store";
import { Store } from "@tanstack/store";
import "./App.css";

const countStore = new Store(0);

function App() {
  const count = useStore(countStore);
  return (
    <div>
      <button onClick={() => countStore.setState((n) => n + 1)}>
        Increment - {count}
      </button>
    </div>
  );
}

export default App;
```

One of the many nice features of TanStack Store is the ability to derive state from other state. That derived state will update when the base state updates.

Let's check this out by doubling the count using derived state.

```tsx
import { useStore } from "@tanstack/react-store";
import { Store, Derived } from "@tanstack/store";
import "./App.css";

const countStore = new Store(0);

const doubledStore = new Derived({
  fn: () => countStore.state * 2,
  deps: [countStore],
});
doubledStore.mount();

function App() {
  const count = useStore(countStore);
  const doubledCount = useStore(doubledStore);

  return (
    <div>
      <button onClick={() => countStore.setState((n) => n + 1)}>
        Increment - {count}
      </button>
      <div>Doubled - {doubledCount}</div>
    </div>
  );
}

export default App;
```

We use the `Derived` class to create a new store that is derived from another store. The `Derived` class has a `mount` method that will start the derived store updating.

Once we've created the derived store we can use it in the `App` component just like we would any other store using the `useStore` hook.

You can find out everything you need to know on how to use TanStack Store in the [TanStack Store documentation](https://tanstack.com/store/latest).

# Demo files

Files prefixed with `demo` can be safely deleted. They are there to provide a starting point for you to play around with the features you've installed.

# Learn More

You can learn more about all of the offerings from TanStack in the [TanStack documentation](https://tanstack.com).
