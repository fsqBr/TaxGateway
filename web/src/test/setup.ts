import '@testing-library/jest-dom/vitest';
import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

// jsdom não implementa rolagem.
window.scrollTo = () => {};

afterEach(() => {
  cleanup();
  sessionStorage.clear();
  localStorage.clear();
});
