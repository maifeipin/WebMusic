import { useEffect, useState } from 'react';
import { getUsers, adminResetPassword } from '../services/api';
import { useAuth } from '../context/AuthContext';
import { Shield, Key, User } from 'lucide-react';

export default function AdminPage() {
    const { username } = useAuth();
    const isAdmin = username === 'admin';
    const [users, setUsers] = useState<{ id: number, username: string }[]>([]);
    const [loading, setLoading] = useState(false);

    useEffect(() => {
        if (isAdmin) {
            setLoading(true);
            getUsers()
                .then(setUsers)
                .catch(err => {
                    console.error(err);
                    // Don't alert on load, just log
                })
                .finally(() => setLoading(false));
        }
    }, [isAdmin]);

    const handleReset = async (userId: number, userName: string) => {
        const newPass = prompt(`Enter new password for user '${userName}':`);
        if (!newPass) return;

        try {
            await adminResetPassword(userId, newPass);
            alert("Password reset successfully.");
        } catch (e: any) {
            alert("Failed: " + (e.response?.data || e.message));
        }
    };

    if (!isAdmin) {
        return (
            <div className="h-full flex items-center justify-center text-gray-500">
                <div className="text-center">
                    <Shield size={48} className="mx-auto mb-4 opacity-20" />
                    <h2 className="text-xl font-bold text-gray-400">Restricted Access</h2>
                    <p className="text-sm mt-2">Only administrators can view this page.</p>
                </div>
            </div>
        );
    }

    return (
        <div className="h-full flex flex-col p-8 space-y-6 overflow-y-auto">
            <header>
                <h1 className="text-3xl font-bold text-white flex items-center gap-2">
                    <Shield className="text-red-500" /> Admin Console
                </h1>
                <p className="text-gray-400 mt-1">Manage users and system security</p>
            </header>

            <div className="bg-gray-900 border border-gray-800 rounded-2xl p-6 max-w-2xl">
                <h2 className="text-xl font-bold text-white mb-4 flex items-center gap-2">
                    <User size={20} /> User Management
                </h2>

                {loading ? (
                    <div className="text-gray-500 text-sm">Loading users...</div>
                ) : (
                    <div className="space-y-2">
                        {users.map(u => (
                            <div key={u.id} className="flex items-center justify-between p-3 bg-gray-800 rounded-lg hover:bg-gray-750 transition">
                                <div className="flex items-center gap-3">
                                    <div className={`w-8 h-8 rounded-full flex items-center justify-center text-sm font-bold ${u.username === 'admin' ? 'bg-red-500/20 text-red-500' : 'bg-gray-700 text-gray-300'}`}>
                                        {u.username[0].toUpperCase()}
                                    </div>
                                    <div>
                                        <div className="font-bold text-white flex items-center gap-2">
                                            {u.username}
                                            {u.username === 'admin' && <span className="text-[10px] bg-red-500/10 text-red-500 px-1.5 py-0.5 rounded border border-red-500/20">ADMIN</span>}
                                        </div>
                                        <div className="text-xs text-gray-500">ID: {u.id}</div>
                                    </div>
                                </div>
                                <button
                                    onClick={() => handleReset(u.id, u.username)}
                                    className="px-3 py-1.5 bg-gray-700 hover:bg-red-600 hover:text-white text-gray-300 rounded-lg text-xs font-bold transition flex items-center gap-1"
                                >
                                    <Key size={14} /> Reset
                                </button>
                            </div>
                        ))}
                    </div>
                )}
            </div>
        </div>
    );
}
