import React, { createContext, useContext, useState } from 'react';

interface AuthContextType {
    token: string | null;
    login: (token: string) => void;
    logout: () => void;
    isAuthenticated: boolean;
    username: string;
}

const AuthContext = createContext<AuthContextType | null>(null);

export const AuthProvider = ({ children }: { children: React.ReactNode }) => {
    const [token, setToken] = useState<string | null>(localStorage.getItem('token'));
    const [username, setUsername] = useState<string>('User');

    // Helper to decode JWT payload safely
    const parseUserFromToken = (jwt: string | null) => {
        if (!jwt) return 'User';
        try {
            const base64Url = jwt.split('.')[1];
            const base64 = base64Url.replace(/-/g, '+').replace(/_/g, '/');
            const jsonPayload = decodeURIComponent(window.atob(base64).split('').map(function (c) {
                return '%' + ('00' + c.charCodeAt(0).toString(16)).slice(-2);
            }).join(''));
            const payload = JSON.parse(jsonPayload);
            // Look for standard claims: "unique_name", "sub", or "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"
            return payload.unique_name || payload.name || payload.sub || payload["http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name"] || 'User';
        } catch (e) {
            console.error("Failed to parse token", e);
            return 'User';
        }
    };

    // Initial load
    React.useEffect(() => {
        if (token) {
            setUsername(parseUserFromToken(token));
        }
    }, [token]);

    const login = (newToken: string) => {
        setToken(newToken);
        localStorage.setItem('token', newToken);
        setUsername(parseUserFromToken(newToken));
    };

    const logout = () => {
        setToken(null);
        localStorage.removeItem('token');
        setUsername('User');
    };

    return (
        <AuthContext.Provider value={{ token, login, logout, isAuthenticated: !!token, username }}>
            {children}
        </AuthContext.Provider>
    );
};

export const useAuth = () => {
    const context = useContext(AuthContext);
    if (!context) throw new Error('useAuth must be used within AuthProvider');
    return context;
};
