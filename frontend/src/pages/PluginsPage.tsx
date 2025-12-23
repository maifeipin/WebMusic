import React, { useEffect, useState } from 'react';
import { getPlugins, addPlugin, deletePlugin, type PluginDefinition } from '../services/api';
import { useAuth } from '../context/AuthContext';
import { Trash2, Plus, ExternalLink, Box, Puzzle, AlertCircle } from 'lucide-react';
import { useNavigate } from 'react-router-dom';

const PluginsPage = () => {
    const { username } = useAuth();
    const isAdmin = username === 'admin';
    const navigate = useNavigate();

    const [plugins, setPlugins] = useState<PluginDefinition[]>([]);
    const [loading, setLoading] = useState(true);
    const [showModal, setShowModal] = useState(false);

    // Form
    const [form, setForm] = useState({
        name: '',
        description: '',
        baseUrl: '',
        entryPath: '/',
        icon: 'Puzzle'
    });

    useEffect(() => {
        loadPlugins();
    }, []);

    const loadPlugins = async () => {
        setLoading(true);
        try {
            const data = await getPlugins();
            setPlugins(data);
        } catch (err) {
            console.error(err);
        } finally {
            setLoading(false);
        }
    };

    const handleDelete = async (id: number) => {
        if (!confirm("Are you sure you want to remove this plugin?")) return;
        try {
            await deletePlugin(id);
            loadPlugins();
        } catch (err) {
            alert("Failed to delete plugin");
        }
    };

    const handleAdd = async (e: React.FormEvent) => {
        e.preventDefault();
        try {
            await addPlugin(form);
            setShowModal(false);
            setForm({ name: '', description: '', baseUrl: '', entryPath: '/', icon: 'Puzzle' });
            loadPlugins();
        } catch (err) {
            alert("Failed to add plugin");
        }
    };

    return (
        <div className="p-8 max-w-7xl mx-auto animate-fade-in">
            <div className="flex justify-between items-center mb-8">
                <div>
                    <h1 className="text-3xl font-bold text-white flex items-center gap-3">
                        <Puzzle className="text-purple-500" />
                        Extensions
                    </h1>
                    <p className="text-gray-400 mt-1">Manage external tools and plugins.</p>
                </div>
                {isAdmin && (
                    <button
                        onClick={() => setShowModal(true)}
                        className="px-4 py-2 bg-indigo-600 hover:bg-indigo-500 text-white rounded-lg flex items-center gap-2 transition shadow-lg shadow-indigo-500/20"
                    >
                        <Plus size={18} /> Install Plugin
                    </button>
                )}
            </div>

            {loading ? (
                <div className="text-center text-gray-500 py-20">Loading...</div>
            ) : (
                <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
                    {plugins.map(plugin => (
                        <div key={plugin.id} className="group bg-gray-800/50 hover:bg-gray-800 border border-white/5 hover:border-purple-500/30 rounded-xl p-6 transition-all duration-300 relative">
                            {isAdmin && (
                                <button
                                    onClick={(e) => { e.stopPropagation(); handleDelete(plugin.id); }}
                                    className="absolute top-4 right-4 p-2 text-gray-500 hover:text-red-400 hover:bg-red-500/10 rounded-full transition opacity-0 group-hover:opacity-100"
                                >
                                    <Trash2 size={16} />
                                </button>
                            )}

                            <div className="flex items-start gap-4 mb-4">
                                <div className="w-12 h-12 bg-purple-500/20 rounded-lg flex items-center justify-center text-purple-400">
                                    <Box size={24} />
                                </div>
                                <div className="flex-1 overflow-hidden">
                                    <h3 className="text-lg font-bold text-white truncate">{plugin.name}</h3>
                                    <p className="text-sm text-gray-400 line-clamp-2 min-h-[2.5em]">{plugin.description}</p>
                                </div>
                            </div>

                            <div className="mt-4 pt-4 border-t border-white/5 flex justify-between items-center">
                                <span className={`text-xs px-2 py-0.5 rounded-full ${plugin.isEnabled ? 'bg-green-900/30 text-green-400' : 'bg-red-900/30 text-red-400'}`}>
                                    {plugin.isEnabled ? 'Active' : 'Disabled'}
                                </span>
                                <button
                                    onClick={() => {
                                        if (plugin.name.toLowerCase().includes('netease') || plugin.name.includes('网易')) {
                                            navigate('/apps/netease');
                                        } else {
                                            navigate(`/apps/${plugin.id}`);
                                        }
                                    }}
                                    className="text-sm text-indigo-400 hover:text-indigo-300 flex items-center gap-1 font-medium"
                                >
                                    Open App <ExternalLink size={14} />
                                </button>
                            </div>
                        </div>
                    ))}

                    {plugins.length === 0 && (
                        <div className="col-span-full py-20 text-center border-2 border-dashed border-gray-800 rounded-xl text-gray-500 flex flex-col items-center gap-4">
                            <Puzzle size={48} className="opacity-20" />
                            <p>No extensions installed.</p>
                        </div>
                    )}
                </div>
            )}

            {/* Add Modal */}
            {showModal && (
                <div className="fixed inset-0 bg-black/80 backdrop-blur-sm z-50 flex items-center justify-center p-4">
                    <div className="bg-gray-900 border border-gray-700 rounded-xl w-full max-w-md p-6 shadow-2xl">
                        <h2 className="text-xl font-bold text-white mb-4">Install Extension</h2>
                        <form onSubmit={handleAdd} className="space-y-4">
                            <div>
                                <label className="block text-xs text-gray-400 mb-1">Name</label>
                                <input required type="text" className="w-full bg-gray-800 border border-gray-700 rounded p-2 text-white text-sm"
                                    value={form.name} onChange={e => setForm({ ...form, name: e.target.value })} placeholder="e.g. Netease Metadata" />
                            </div>
                            <div>
                                <label className="block text-xs text-gray-400 mb-1">Description</label>
                                <textarea className="w-full bg-gray-800 border border-gray-700 rounded p-2 text-white text-sm"
                                    value={form.description} onChange={e => setForm({ ...form, description: e.target.value })} placeholder="What does this tool do?" />
                            </div>
                            <div>
                                <label className="block text-xs text-gray-400 mb-1">Internal URL (Docker Service)</label>
                                <input required type="text" className="w-full bg-gray-800 border border-gray-700 rounded p-2 text-white text-sm font-mono"
                                    value={form.baseUrl} onChange={e => setForm({ ...form, baseUrl: e.target.value })} placeholder="http://container-name:port" />
                                <p className="text-[10px] text-gray-500 mt-1 flex items-center gap-1"><AlertCircle size={10} /> Only accessible by Backend</p>
                            </div>
                            <div>
                                <label className="block text-xs text-gray-400 mb-1">Entry Path (UI)</label>
                                <input type="text" className="w-full bg-gray-800 border border-gray-700 rounded p-2 text-white text-sm font-mono"
                                    value={form.entryPath} onChange={e => setForm({ ...form, entryPath: e.target.value })} placeholder="/" />
                            </div>

                            <div className="flex justify-end gap-3 mt-6">
                                <button type="button" onClick={() => setShowModal(false)} className="px-4 py-2 text-gray-400 hover:text-white">Cancel</button>
                                <button type="submit" className="px-4 py-2 bg-indigo-600 hover:bg-indigo-500 text-white rounded shadow-lg">Install</button>
                            </div>
                        </form>
                    </div>
                </div>
            )}
        </div>
    );
};

export default PluginsPage;
