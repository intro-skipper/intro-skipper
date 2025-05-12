import { RouterProvider } from '@tanstack/react-router';
import { QueryClientProvider } from '@tanstack/react-query';
import { ReactQueryDevtools } from '@tanstack/react-query-devtools';
import { ConfigProvider } from './context/ConfigContext';
import { queryClient } from './lib/queryClient';
import router from './lib/router';
import './App.css';
import './styles/app.css';

function App() {
  return (
    <QueryClientProvider client={queryClient}>
      <ConfigProvider>
        <RouterProvider router={router} />
      </ConfigProvider>
      <ReactQueryDevtools initialIsOpen={false} />
    </QueryClientProvider>
  );
}

export default App;
