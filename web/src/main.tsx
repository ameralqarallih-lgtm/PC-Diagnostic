import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider } from '@tanstack/react-router';
import { MotionConfig } from 'motion/react';
import { StrictMode } from 'react';
import { ConfirmProvider } from '@/components/ui/confirm';
import { ToastProvider } from '@/components/ui/toast';
import { createRoot } from 'react-dom/client';
import { ApiError } from '@/lib/api/client';
import { installClientErrorReporting } from '@/features/reports/ReportProblem';
import { applyDensity, loadDensity } from '@/lib/look';
import { applyTheme, loadTheme } from '@/lib/theme';
import { createAppRouter } from '@/router';
import './index.css';

applyTheme(loadTheme());
applyDensity(loadDensity());
installClientErrorReporting();

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // A 401 means the session ended; retrying cannot fix it.
      retry: (failureCount, error) => !(error instanceof ApiError && error.isUnauthorized) && failureCount < 2,
      refetchOnWindowFocus: true,
    },
  },
});

const router = createAppRouter();

const root = document.getElementById('root');
if (!root) {
  throw new Error('Missing #root element');
}

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <MotionConfig reducedMotion="user">
        <ToastProvider>
          <ConfirmProvider>
          <RouterProvider router={router} />
          </ConfirmProvider>
        </ToastProvider>
      </MotionConfig>
    </QueryClientProvider>
  </StrictMode>,
);
