import React, { useEffect, useState } from 'react';
import { getSources, addSource, deleteSource, startScan, getCredentials, addCredential, deleteCredential, testCredential, getScanStatus } from '../services/api';
import { Plus, Trash, Play, Key, Check, X, FolderSearch } from 'lucide-react';
import DirectoryBrowser from '../components/DirectoryBrowser';

export default function Sources() {
    const [sources, setSources] = useState<any[]>([]);
    const [credentials, setCredentials] = useState<any[]>([]);

    // Source State
    const [newShare, setNewShare] = useState(''); // Share Name
    const [newName, setNewName] = useState('');
    const [selectedCred, setSelectedCred] = useState<string>('');
    const [scanning, setScanning] = useState<number | null>(null);
    const [showBrowser, setShowBrowser] = useState(false);

    // Credential State
    const [credName, setCredName] = useState('HomeNAS');
    const [credProvider, setCredProvider] = useState('SMB');
    const [credHost, setCredHost] = useState('192.168.2.18');
    const [credUser, setCredUser] = useState('adminn');
    const [credPass, setCredPass] = useState('Nnnnn');
    const [showCredForm, setShowCredForm] = useState(false);
    const [testStatus, setTestStatus] = useState<'idle' | 'testing' | 'success' | 'failure'>('idle');

    useEffect(() => {
        loadData();
    }, []);

    const loadData = () => {
        getSources().then(res => setSources(res.data));
        getCredentials().then(res => setCredentials(res.data));
    };

    const handleAddSource = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!newShare || !newName || !selectedCred) {
            alert("Please provide Name, Share, and select a Credential.");
            return;
        }
        try {
            await addSource({
                name: newName,
                path: newShare,
                storageCredentialId: parseInt(selectedCred)
            });
            setNewShare('');
            setNewName('');
            setSelectedCred('');
            loadData();
        } catch (error: any) {
            if (error.response && error.response.status === 409) {
                // Duplicate/Nested warning
                const msg = error.response.data.message || "Path exists or overlaps. Continue?";
                if (confirm(msg)) {
                    try {
                        await addSource({
                            name: newName,
                            path: newShare,
                            storageCredentialId: parseInt(selectedCred)
                        }, true); // force=true
                        setNewShare('');
                        setNewName('');
                        setSelectedCred('');
                        loadData();
                    } catch (e) {
                        alert("Failed to force add source.");
                    }
                }
            } else {
                console.error(error);
                alert(error.response?.data || "Error adding source");
            }
        }
    };

    const handleTestCred = async () => {
        setTestStatus('testing');
        try {
            const authData = JSON.stringify({ username: credUser, password: credPass });
            const res = await testCredential({
                name: 'Test',
                providerType: credProvider,
                host: credHost,
                authData
            });
            if (res.data.success) setTestStatus('success');
            else setTestStatus('failure');
        } catch {
            setTestStatus('failure');
        }
    };

    const handleAddCred = async (e: React.FormEvent) => {
        e.preventDefault();
        if (!credName || !credHost) return;
        try {
            const authData = JSON.stringify({ username: credUser, password: credPass });
            await addCredential({
                name: credName,
                providerType: credProvider,
                host: credHost,
                authData
            });
            setCredName('');
            setCredHost('');
            setCredUser('');
            setCredPass('');
            setShowCredForm(false);
            setTestStatus('idle');
            loadData();
        } catch (error) {
            console.error(error);
        }
    };

    const handleDelete = async (id: number) => {
        if (!confirm('Delete source?')) return;
        await deleteSource(id);
        loadData();
    };

    const handleDeleteCred = async (id: number) => {
        if (!confirm('Delete credential?')) return;
        await deleteCredential(id);
        loadData();
    };

    // Scan Status Polling
    const [scanStatus, setScanStatus] = useState<any>(null);

    // Dedicated Polling Effect
    useEffect(() => {
        const interval = setInterval(async () => {
            try {
                const res = await getScanStatus();
                setScanStatus(res.data);
                if (res.data.isScanning) {
                    setScanning(res.data.currentSourceId);
                } else {
                    setScanning(null);
                }
            } catch (e) {
                console.error("Poll failed", e);
            }
        }, 2000);
        return () => clearInterval(interval);
    }, []);

    const handleScan = async (id: number) => {
        try {
            await startScan(id);
            // Don't alert immediately, let polling show status
        } catch (error) {
            alert('Failed to start scan');
        }
    };

    // Callback when path is selected from browser
    const handlePathSelect = (path: string) => {
        setNewShare(path);
        setShowBrowser(false);
        // Auto-set Name if empty
        if (!newName) {
            const parts = path.split('/');
            setNewName(parts[parts.length - 1]);
        }
    };

    return (
        <div className="p-8">
            <h1 className="text-3xl font-bold mb-8">Connection Manager</h1>

            {/* Global Scan Status Banner */}
            {scanStatus && scanStatus.isScanning && (
                <div className="mb-8 p-4 bg-blue-900/30 border border-blue-800 rounded-xl flex items-center justify-between animate-pulse">
                    <div className="flex items-center gap-3">
                        <div className="w-5 h-5 border-2 border-blue-400 border-t-transparent rounded-full animate-spin" />
                        <div>
                            <p className="text-blue-200 font-medium">{scanStatus.statusMessage}</p>
                            <p className="text-xs text-blue-400">Processed {scanStatus.itemsProcessed} items...</p>
                        </div>
                    </div>
                </div>
            )}

            {/* Error Banner */}
            {scanStatus && scanStatus.error && !scanStatus.isScanning && (
                <div className="mb-8 p-4 bg-red-900/30 border border-red-800 rounded-xl flex items-center gap-3">
                    <X className="text-red-400" />
                    <div>
                        <p className="text-red-200 font-medium">Last Scan Failed</p>
                        <p className="text-xs text-red-400">{scanStatus.error}</p>
                    </div>
                </div>
            )}

            {/* Credentials Section */}
            <div className="mb-12 border-b border-gray-800 pb-8">
                <div className="flex items-center justify-between mb-4">
                    <h2 className="text-xl font-semibold flex items-center gap-2">
                        <Key className="text-blue-500" /> Connection Profiles
                    </h2>
                    <button onClick={() => setShowCredForm(!showCredForm)} className="text-sm text-blue-400 hover:text-blue-300">
                        {showCredForm ? 'Cancel' : '+ New Profile'}
                    </button>
                </div>

                {showCredForm && (
                    <div className="bg-gray-800 p-6 rounded-xl mb-6">
                        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4 mb-4">
                            <div>
                                <label className="block text-gray-400 text-sm mb-1">Friendly Name</label>
                                <input value={credName} onChange={e => setCredName(e.target.value)} className="w-full bg-gray-700 rounded p-2 text-white" placeholder="Home NAS" />
                            </div>
                            <div>
                                <label className="block text-gray-400 text-sm mb-1">Provider</label>
                                <select value={credProvider} onChange={e => setCredProvider(e.target.value)} className="w-full bg-gray-700 rounded p-2 text-white h-10">
                                    <option value="SMB">SMB (Windows Share)</option>
                                    <option value="GDRIVE">Google Drive (Coming Soon)</option>
                                    <option value="BAIDU">Baidu Netdisk (Coming Soon)</option>
                                </select>
                            </div>
                            <div>
                                <label className="block text-gray-400 text-sm mb-1">Host IP / Domain</label>
                                <input value={credHost} onChange={e => setCredHost(e.target.value)} className="w-full bg-gray-700 rounded p-2 text-white" placeholder="192.168.1.100" />
                            </div>
                            <div>
                                <label className="block text-gray-400 text-sm mb-1">Username</label>
                                <input value={credUser} onChange={e => setCredUser(e.target.value)} className="w-full bg-gray-700 rounded p-2 text-white" />
                            </div>
                            <div>
                                <label className="block text-gray-400 text-sm mb-1">Password</label>
                                <input type="password" value={credPass} onChange={e => setCredPass(e.target.value)} className="w-full bg-gray-700 rounded p-2 text-white" />
                            </div>
                        </div>

                        <div className="flex gap-4">
                            <button onClick={handleTestCred} type="button" className={`p-2 rounded flex items-center gap-2 px-4 h-10 ${testStatus === 'success' ? 'bg-green-600' : testStatus === 'failure' ? 'bg-red-600' : 'bg-gray-600 hover:bg-gray-500'}`}>
                                {testStatus === 'testing' ? 'Testing...' : testStatus === 'success' ? <><Check size={18} /> Valid</> : testStatus === 'failure' ? <><X size={18} /> Failed</> : 'Test Connection'}
                            </button>
                            <button onClick={handleAddCred} className="bg-blue-600 hover:bg-blue-500 text-white p-2 rounded flex items-center gap-2 px-4 h-10">
                                Save Profile
                            </button>
                        </div>
                    </div>
                )}

                <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
                    {credentials.map(cred => (
                        <div key={cred.id} className="bg-gray-900 border border-gray-800 p-4 rounded-lg flex flex-col gap-2">
                            <div className="flex justify-between items-start">
                                <span className="font-bold text-gray-200">{cred.name}</span>
                                <button onClick={() => handleDeleteCred(cred.id)} className="text-red-500 hover:text-red-400"><Trash size={14} /></button>
                            </div>
                            <div className="text-xs text-gray-500 bg-gray-950 p-2 rounded">
                                <span className="font-mono text-blue-400">{cred.providerType}</span> : <span className="text-gray-300">{cred.host}</span>
                            </div>
                        </div>
                    ))}
                    {credentials.length === 0 && <p className="text-gray-500 text-sm">No connection profiles added.</p>}
                </div>
            </div>

            {/* Sources Section */}
            <h2 className="text-xl font-semibold mb-4">Scan Sources</h2>
            <form onSubmit={handleAddSource} className="bg-gray-800 p-6 rounded-xl mb-8 flex gap-4 items-end flex-wrap">
                <div className="flex-1 min-w-[200px]">
                    <label className="block text-gray-400 text-sm mb-1">Friendly Name</label>
                    <input value={newName} onChange={e => setNewName(e.target.value)} className="w-full bg-gray-700 rounded p-2 text-white" placeholder="My Music" />
                </div>
                <div className="flex-1 min-w-[200px]">
                    <label className="block text-gray-400 text-sm mb-1">Connection Profile</label>
                    <select value={selectedCred} onChange={e => setSelectedCred(e.target.value)} className="w-full bg-gray-700 rounded p-2 text-white h-10">
                        <option value="">Select Profile...</option>
                        {credentials.map(c => <option key={c.id} value={c.id}>{c.name} ({c.host})</option>)}
                    </select>
                </div>
                <div className="flex-1 min-w-[200px] flex gap-2 items-end">
                    <div className="flex-1">
                        <label className="block text-gray-400 text-sm mb-1">Share Name / Path</label>
                        <input value={newShare} onChange={e => setNewShare(e.target.value)} className="w-full bg-gray-700 rounded p-2 text-white" placeholder="Music" />
                    </div>
                    <button
                        type="button"
                        onClick={() => setShowBrowser(true)}
                        disabled={!selectedCred}
                        title={!selectedCred ? "Select a profile first" : "Browse"}
                        className="bg-gray-700 hover:bg-gray-600 p-2 rounded h-10 text-gray-300 disabled:opacity-50"
                    >
                        <FolderSearch size={20} />
                    </button>
                </div>
                <button type="submit" className="bg-green-600 hover:bg-green-500 text-white p-2 rounded flex items-center gap-2 px-4 h-10">
                    <Plus size={18} /> Add Source
                </button>
            </form>

            {/* Directory Browser Modal */}
            {showBrowser && selectedCred && (
                <DirectoryBrowser
                    credentialId={parseInt(selectedCred)}
                    initialPath={newShare}
                    onSelect={handlePathSelect}
                    onClose={() => setShowBrowser(false)}
                />
            )}

            {!showBrowser && (
                <div className="grid gap-4">
                    {sources.map(source => (
                        <div key={source.id} className="bg-gray-800 p-4 rounded-xl flex items-center justify-between">
                            <div>
                                <h3 className="font-bold text-lg">{source.name}</h3>
                                <p className="text-gray-400 font-mono text-sm">{source.path}</p>
                                {/* Resolve Cred Name */}
                                {source.storageCredentialId && (
                                    <span className="text-xs bg-blue-900 text-blue-300 px-2 py-1 rounded mt-1 inline-block">
                                        Via: {credentials.find(c => c.id === source.storageCredentialId)?.name || 'Unknown Profile'}
                                    </span>
                                )}
                            </div>
                            <div className="flex items-center gap-2">
                                <div className="text-right mr-4">
                                    <div className="text-xs text-gray-500">Last Scan</div>
                                    {/* Placeholder for last scan time if we had it */}
                                </div>
                                <button
                                    onClick={() => handleScan(source.id)}
                                    disabled={scanning === source.id}
                                    className={`p-2 rounded flex items-center gap-2 px-4 ${scanning === source.id ? 'bg-gray-600' : 'bg-blue-600 hover:bg-blue-500'}`}
                                >
                                    <Play size={16} /> {scanning === source.id ? 'Scanning...' : 'Scan'}
                                </button>
                                <button onClick={() => handleDelete(source.id)} className="bg-red-600 hover:bg-red-500 p-2 rounded">
                                    <Trash size={16} />
                                </button>
                            </div>
                        </div>
                    ))}
                    {sources.length === 0 && <p className="text-gray-500">No sources configured.</p>}
                </div>
            )}
        </div>
    );
}
