// bigscreen/src/App.test.tsx
import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { App } from './App';

describe('App', () => {
    it('renders the Hello BIFROST heading', () => {
        render(<App />);
        expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(/Hello BIFROST/i);
    });

    it('renders the Phase 10 placeholder subtitle', () => {
        render(<App />);
        expect(screen.getByText(/Phase 10/i)).toBeInTheDocument();
    });
});
