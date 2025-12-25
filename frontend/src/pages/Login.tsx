import React, { useState } from 'react';
import { useAuth } from '../context/AuthContext';
import { login as apiLogin } from '../services/api';
import { useNavigate } from 'react-router-dom';

export default function Login() {
    const [username, setUsername] = useState('');
    const [password, setPassword] = useState('');
    const { login } = useAuth();
    const navigate = useNavigate();
    const [error, setError] = useState('');

    // Captcha State
    const [captchaRequired, setCaptchaRequired] = useState(false);
    const [captchaId, setCaptchaId] = useState('');
    const [captchaText, setCaptchaText] = useState('');
    const [captchaAnswer, setCaptchaAnswer] = useState('');

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        setError('');
        try {
            const res = await apiLogin(username, password,
                captchaRequired ? captchaId : undefined,
                captchaRequired ? captchaAnswer : undefined
            );
            login(res.data.token);
            navigate('/');
        } catch (err: any) {
            const data = err.response?.data;
            if (data && data.captchaRequired) {
                setCaptchaRequired(true);
                setCaptchaId(data.captchaId);
                setCaptchaText(data.captchaText);
                setCaptchaAnswer('');
                setError(data.message || 'Verification required');
            } else {
                setError('Invalid credentials');
                // Check if we need to reset captcha state if it wasn't required? 
                // Mostly safety, but if backend stops asking, we should probably hide it.
                // However, backend logic says if failed >=3 it always asks.
            }
        }
    };

    return (
        <div className="flex items-center justify-center h-screen bg-gray-900 text-white">
            <form onSubmit={handleSubmit} className="bg-gray-800 p-8 rounded-lg shadow-lg w-96 space-y-4">
                <h1 className="text-2xl font-bold text-center mb-6">WebMusic Login</h1>
                {error && <p className="text-red-500 text-center">{error}</p>}
                <div>
                    <label className="block text-sm font-medium mb-1">Username</label>
                    <input
                        type="text"
                        value={username}
                        onChange={e => setUsername(e.target.value)}
                        className="w-full bg-gray-700 rounded p-2 focus:outline-none focus:ring-2 focus:ring-blue-500"
                    />
                </div>
                <div>
                    <label className="block text-sm font-medium mb-1">Password</label>
                    <input
                        type="password"
                        value={password}
                        onChange={e => setPassword(e.target.value)}
                        className="w-full bg-gray-700 rounded p-2 focus:outline-none focus:ring-2 focus:ring-blue-500"
                    />
                </div>
                {captchaRequired && (
                    <div className="animate-fade-in">
                        <label className="block text-sm font-medium mb-1">
                            验证码: <span className="text-yellow-400 font-mono text-lg mx-1">{captchaText}</span>
                            <span className="text-xs text-gray-400">(后数 - 前数)</span>
                        </label>
                        <input
                            type="text"
                            value={captchaAnswer}
                            onChange={e => setCaptchaAnswer(e.target.value)}
                            className="w-full bg-gray-700 rounded p-2 focus:outline-none focus:ring-2 focus:ring-blue-500"
                            placeholder="输入计算结果"
                            autoFocus
                        />
                    </div>
                )}
                <button type="submit" className="w-full bg-blue-600 hover:bg-blue-500 p-2 rounded transition font-bold mt-4">
                    Login
                </button>
            </form>
        </div>
    );
}
