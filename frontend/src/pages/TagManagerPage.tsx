import { Tag } from 'lucide-react';
// import { getSongsByIds, updateMedia } from '../services/api'; // We'll implement batch update later
import BatchProcessor from './tagmanager/BatchProcessor';

export default function TagManagerPage() {
    return (
        <div className="p-8 h-full flex flex-col">
            <div className="flex items-center gap-3 mb-6">
                <Tag size={32} className="text-blue-500" />
                <div>
                    <h1 className="text-3xl font-bold">Tag Manager</h1>
                    <p className="text-gray-400">AI-powered metadata cleanup and organization</p>
                </div>
            </div>

            <div className="flex-1 bg-gray-900 border border-gray-800 rounded-2xl overflow-hidden shadow-2xl">
                <BatchProcessor />
            </div>
        </div>
    );
}
