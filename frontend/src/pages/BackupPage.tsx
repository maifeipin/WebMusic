import { useState } from 'react';
import { Download, Upload, CheckCircle, Database } from 'lucide-react';
import { exportFavorites, importFavorites, exportData, importData } from '../services/api';

export default function BackupPage() {
    // Favorites State
    const [favFile, setFavFile] = useState<File | null>(null);
    const [favText, setFavText] = useState('');
    const [favStatus, setFavStatus] = useState<{ type: 'success' | 'error', msg: string } | null>(null);
    const [favLoading, setFavLoading] = useState(false);

    // Metadata State
    const [metaFile, setMetaFile] = useState<File | null>(null);
    const [importMode, setImportMode] = useState(0); // 0=Append, 1=Update, 2=Overwrite
    const [metaStatus, setMetaStatus] = useState<{ type: 'success' | 'error', msg: string } | null>(null);
    const [metaLoading, setMetaLoading] = useState(false);

    // --- Favorites Logic ---
    const handleFavFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        if (e.target.files && e.target.files[0]) {
            setFavFile(e.target.files[0]);
            setFavText('');
            setFavStatus(null);
        }
    };

    const handleExportFav = async () => {
        try {
            const res = await exportFavorites();
            downloadBlob(res.data, `webmusic_favorites_${dateStr()}.txt`);
        } catch (e) {
            setFavStatus({ type: 'error', msg: 'Export failed.' });
        }
    };

    const handleImportFav = async () => {
        setFavLoading(true);
        setFavStatus(null);
        try {
            let uploadFile = favFile;
            if (!uploadFile && favText.trim()) {
                const blob = new Blob([favText], { type: 'text/plain' });
                uploadFile = new File([blob], "favorites_import.txt", { type: "text/plain" });
            }
            if (!uploadFile) throw new Error("No file or text");

            const formData = new FormData();
            formData.append('file', uploadFile);
            const res = await importFavorites(formData);
            setFavStatus({ type: 'success', msg: `Imported ${res.data.imported} favorites!` });
            setFavFile(null);
            setFavText('');
        } catch (error) {
            setFavStatus({ type: 'error', msg: 'Import failed.' });
        } finally {
            setFavLoading(false);
        }
    };

    // --- Metadata Logic ---
    const handleMetaFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        if (e.target.files && e.target.files[0]) {
            setMetaFile(e.target.files[0]);
            setMetaStatus(null);
        }
    };

    const handleExportMeta = async () => {
        try {
            const res = await exportData();
            downloadBlob(res.data, `webmusic_library_${dateStr()}.json`);
        } catch (e) {
            setMetaStatus({ type: 'error', msg: 'Export failed.' });
        }
    };

    const handleImportMeta = async () => {
        if (!metaFile) return;
        if (importMode === 2) {
            if (!confirm("WARNING: This will DELETE ALL EXISTING SONGS and replace them with the backup. Are you sure?")) return;
        }

        setMetaLoading(true);
        setMetaStatus(null);
        try {
            const formData = new FormData();
            formData.append('file', metaFile);
            const res = await importData(formData, importMode);
            const r = res.data;
            setMetaStatus({ type: 'success', msg: `${r.message}` });
            setMetaFile(null);
        } catch (error: any) {
            const msg = error.response?.data || 'Import failed.';
            setMetaStatus({ type: 'error', msg: typeof msg === 'string' ? msg : 'Import failed.' });
        } finally {
            setMetaLoading(false);
        }
    };

    const downloadBlob = (blob: Blob, name: string) => {
        const url = window.URL.createObjectURL(new Blob([blob]));
        const link = document.createElement('a');
        link.href = url;
        link.setAttribute('download', name);
        document.body.appendChild(link);
        link.click();
        link.remove();
    };

    const dateStr = () => new Date().toISOString().slice(0, 10);

    return (
        <div className="p-4 md:p-8 max-w-4xl mx-auto pb-32 space-y-12">
            <header>
                <h1 className="text-3xl font-bold text-white mb-2">Backup & Restore</h1>
                <p className="text-gray-400">Manage your library metadata and personal favorites.</p>
            </header>

            {/* Section 1: Library Metadata */}
            <section className="space-y-6">
                <div className="flex items-center gap-3 text-purple-400 border-b border-gray-800 pb-2">
                    <Database size={24} />
                    <h2 className="text-xl font-bold">Library Metadata</h2>
                </div>

                <div className="grid md:grid-cols-2 gap-6">
                    {/* Meta Export */}
                    <div className="bg-gray-900 border border-gray-800 rounded-2xl p-6">
                        <h3 className="font-bold text-gray-200 mb-2">Export Library</h3>
                        <p className="text-gray-500 text-sm mb-4">
                            Save a JSON backup of all song metadata (Artist, Title, Album, etc.). Does not include physical files.
                        </p>
                        <button onClick={handleExportMeta} className="w-full py-2 bg-purple-600 hover:bg-purple-500 text-white rounded-lg font-medium transition flex items-center justify-center gap-2">
                            <Download size={18} /> Export JSON
                        </button>
                    </div>

                    {/* Meta Import */}
                    <div className="bg-gray-900 border border-gray-800 rounded-2xl p-6">
                        <h3 className="font-bold text-gray-200 mb-2">Import Library</h3>
                        <div className="space-y-4">
                            <input type="file" accept=".json" onChange={handleMetaFileChange} className="block w-full text-sm text-gray-400 file:mr-4 file:py-2 file:px-4 file:rounded-lg file:border-0 file:bg-gray-800 file:text-purple-400 hover:file:bg-gray-700 cursor-pointer" />

                            <div className="space-y-2">
                                <p className="text-xs text-gray-500 font-bold uppercase">Import Mode:</p>
                                <label className="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                                    <input type="radio" name="mode" checked={importMode === 0} onChange={() => setImportMode(0)} />
                                    <span>Append / Skip Existing</span>
                                </label>
                                <label className="flex items-center gap-2 text-sm text-gray-300 cursor-pointer">
                                    <input type="radio" name="mode" checked={importMode === 1} onChange={() => setImportMode(1)} />
                                    <span>Update Metadata (Match by Path)</span>
                                </label>
                                <label className="flex items-center gap-2 text-sm text-red-300 cursor-pointer">
                                    <input type="radio" name="mode" checked={importMode === 2} onChange={() => setImportMode(2)} />
                                    <span>Clear Database & Overwrite</span>
                                </label>
                            </div>

                            {metaStatus && (
                                <div className={`p-2 rounded text-xs ${metaStatus.type === 'success' ? 'bg-green-900/30 text-green-400' : 'bg-red-900/30 text-red-400'}`}>
                                    {metaStatus.msg}
                                </div>
                            )}

                            <button
                                onClick={handleImportMeta}
                                disabled={!metaFile || metaLoading}
                                className="w-full py-2 bg-purple-600 hover:bg-purple-500 disabled:bg-gray-800 disabled:text-gray-600 text-white rounded-lg font-medium transition flex items-center justify-center gap-2"
                            >
                                {metaLoading ? 'Procesing...' : <><Upload size={18} /> Start Import</>}
                            </button>
                        </div>
                    </div>
                </div>
            </section>

            {/* Section 2: Favorites */}
            <section className="space-y-6 opacity-80 hover:opacity-100 transition">
                <div className="flex items-center gap-3 text-pink-400 border-b border-gray-800 pb-2">
                    <CheckCircle size={24} />
                    <h2 className="text-xl font-bold">Favorites List</h2>
                </div>

                <div className="bg-gray-900 border border-gray-800 rounded-2xl p-6">
                    <div className="grid md:grid-cols-2 gap-8">
                        <div>
                            <h3 className="font-bold text-gray-200 mb-2">Export List</h3>
                            <button onClick={handleExportFav} className="w-full py-2 bg-gray-800 hover:bg-gray-700 text-white rounded-lg font-medium transition flex items-center justify-center gap-2 mb-4">
                                <Download size={18} /> Download .txt
                            </button>
                            <p className="text-gray-500 text-xs">Simple text list of file paths.</p>
                        </div>

                        <div>
                            <h3 className="font-bold text-gray-200 mb-2">Import List</h3>
                            <div className="space-y-3">
                                <input type="file" accept=".txt" onChange={handleFavFileChange} className="block w-full text-sm text-gray-400 file:mr-4 file:py-2 file:px-4 file:rounded-lg file:border-0 file:bg-gray-800 file:text-pink-400 hover:file:bg-gray-700 cursor-pointer" />
                                <div className="text-center text-gray-600 text-xs font-bold">- OR -</div>
                                <textarea
                                    value={favText}
                                    onChange={e => setFavText(e.target.value)}
                                    placeholder="Paste paths here..."
                                    className="w-full h-20 bg-black/30 border border-gray-700 rounded-lg p-2 text-xs text-gray-300 font-mono focus:outline-none focus:border-pink-500"
                                />
                                {favStatus && (
                                    <div className={`p-2 rounded text-xs ${favStatus.type === 'success' ? 'bg-green-900/30 text-green-400' : 'bg-red-900/30 text-red-400'}`}>
                                        {favStatus.msg}
                                    </div>
                                )}
                                <button
                                    onClick={handleImportFav}
                                    disabled={(!favFile && !favText) || favLoading}
                                    className="w-full py-2 bg-pink-600 hover:bg-pink-500 disabled:bg-gray-800 disabled:text-gray-600 text-white rounded-lg font-medium transition flex items-center justify-center gap-2"
                                >
                                    {favLoading ? 'Importing...' : <><Upload size={18} /> Import Favorites</>}
                                </button>
                            </div>
                        </div>
                    </div>
                </div>
            </section>

        </div>
    );
}
